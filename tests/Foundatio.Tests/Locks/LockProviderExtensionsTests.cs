using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Lock;
using Foundatio.Utility;
using Foundatio.Xunit;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Foundatio.Tests.Locks;

public class LockProviderExtensionsTests(ITestOutputHelper output) : TestWithLoggingBase(output)
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task TryAcquireAsync_WhenClockMovesBackward_DoesNotReturnExpiredLeases(int resourceCount)
    {
        var leaseClock = new FakeTimeProvider();
        var clockAdjustment = TimeSpan.Zero;
        var hostClock = new Mock<TimeProvider>();
        hostClock.Setup(t => t.GetUtcNow()).Returns(() => leaseClock.GetUtcNow() - clockAdjustment);
        hostClock.Setup(t => t.GetTimestamp()).Returns(leaseClock.GetTimestamp);
        hostClock.SetupGet(t => t.TimestampFrequency).Returns(leaseClock.TimestampFrequency);

        using var cache = new InMemoryCacheClient(o => o.TimeProvider(leaseClock).LoggerFactory(Log));
        var inner = new CacheLockProvider(cache, null, leaseClock, null, Log);
        var provider = new Mock<ILockProvider>();
        provider.As<IHaveTimeProvider>().SetupGet(p => p.TimeProvider).Returns(hostClock.Object);
        string[] resources = Enumerable.Range(0, resourceCount).Select(i => i.ToString()).ToArray();
        ILock? replacement = null;
        provider.Setup(p => p.TryAcquireAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(async (string resource, TimeSpan? duration, bool release, CancellationToken token) =>
            {
                var acquired = await inner.TryAcquireAsync(resource, duration, release, token);
                if (resource == resources[^1])
                {
                    leaseClock.Advance(TimeSpan.FromSeconds(61));
                    clockAdjustment = TimeSpan.FromMinutes(2);
                    replacement = await inner.AcquireAsync(resources[0], TimeSpan.FromMinutes(10), cancellationToken: token);
                }

                return acquired;
            });

        try
        {
            await using var acquired = await provider.Object.TryAcquireAsync(resources, TimeSpan.FromMinutes(1), cancellationToken: TestCancellationToken);

            Assert.Null(acquired);
            Assert.NotNull(replacement);
            await replacement.RenewAsync();
            Assert.True(await inner.IsLockedAsync(resources[0]));
            foreach (string resource in resources.Skip(1))
                Assert.False(await inner.IsLockedAsync(resource));
        }
        finally
        {
            if (replacement is not null)
                await replacement.ReleaseAsync();
        }
    }
}

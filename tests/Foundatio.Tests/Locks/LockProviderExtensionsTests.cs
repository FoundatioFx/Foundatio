using System;
using System.Collections.Generic;
using System.Globalization;
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
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AcquireAsync_WhenCacheResponseOutlastsLease_DoesNotReturnExpiredHandle(bool tryAcquire, bool replaced)
    {
        var clock = new FakeTimeProvider();
        using var cache = new InMemoryCacheClient(o => o.TimeProvider(clock).LoggerFactory(Log));
        var delayedCache = new Mock<ICacheClient>();
        var response = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var added = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        delayedCache.Setup(c => c.AddAsync("lock:resource", It.IsAny<string>(), It.IsAny<TimeSpan?>()))
            .Returns(async (string key, string value, TimeSpan? duration) =>
            {
                bool result = await cache.AddAsync(key, value, duration);
                added.TrySetResult();
                await response.Task;
                return result;
            });
        delayedCache.Setup(c => c.RemoveIfEqualAsync("lock:resource", It.IsAny<string>()))
            .Returns((string key, string value) => cache.RemoveIfEqualAsync(key, value));
        var provider = new CacheLockProvider(delayedCache.Object, null, clock, null, Log);
        var acquisitionToken = new CancellationToken(true);
        ILock? acquired = null;
        var acquisition = AcquireAsync();

        try
        {
            await added.Task.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);
            clock.Advance(TimeSpan.FromSeconds(61));
            if (replaced)
                Assert.True(await cache.AddAsync("lock:resource", "replacement", TimeSpan.FromMinutes(10)));

            response.TrySetResult();
            if (tryAcquire)
            {
                await acquisition.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);
                Assert.Null(acquired);
            }
            else
                await Assert.ThrowsAsync<LockAcquisitionTimeoutException>(() => acquisition.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken));

            var owner = await cache.GetAsync<string>("lock:resource");
            Assert.Equal(replaced, owner.HasValue);
            if (replaced)
                Assert.Equal("replacement", owner.Value);
        }
        finally
        {
            response.TrySetResult();
            try
            {
                await acquisition;
                if (acquired is not null)
                    await acquired.ReleaseAsync();
            }
            catch (LockAcquisitionTimeoutException) { }
        }

        async Task AcquireAsync()
        {
            acquired = tryAcquire
                ? await provider.TryAcquireAsync("resource", TimeSpan.FromMinutes(1), cancellationToken: acquisitionToken)
                : await provider.AcquireAsync("resource", TimeSpan.FromMinutes(1), cancellationToken: acquisitionToken);
        }
    }

    [Fact]
    public async Task TryAcquireAsync_WithDifferentCultures_UsesConsistentResourceOrder()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var acquisitionOrders = new List<string[]>();
        try
        {
            foreach (string cultureName in new[] { "en-US", "sv-SE" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
                var resources = new List<string>();
                var provider = new Mock<ILockProvider>();
                provider.Setup(p => p.TryAcquireAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                    .Returns((string resource, TimeSpan? duration, bool release, CancellationToken token) =>
                    {
                        resources.Add(resource);
                        var acquired = new Mock<ILock>();
                        acquired.SetupGet(l => l.Resource).Returns(resource);
                        acquired.SetupGet(l => l.LockId).Returns(resource);
                        acquired.Setup(l => l.ReleaseAsync()).Returns(Task.CompletedTask);
                        return Task.FromResult<ILock?>(acquired.Object);
                    });

                await using var acquired = await provider.Object.TryAcquireAsync(["ä", "z", "a", "ä"], TimeSpan.Zero, cancellationToken: TestCancellationToken);

                Assert.NotNull(acquired);
                Assert.Equal(3, resources.Count);
                acquisitionOrders.Add(resources.ToArray());
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }

        Assert.Equal(acquisitionOrders[0], acquisitionOrders[1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task AcquireAsync_WithSupportedShortDuration_PreservesExpirationSemantics(int milliseconds)
    {
        var clock = new FakeTimeProvider();
        using var cache = new InMemoryCacheClient(o => o.TimeProvider(clock).LoggerFactory(Log));
        var provider = new CacheLockProvider(cache, null, clock, null, Log);

        await using var acquired = await provider.AcquireAsync("resource", TimeSpan.FromMilliseconds(milliseconds), cancellationToken: TestCancellationToken);

        Assert.True(await provider.IsLockedAsync("resource"));
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(milliseconds == 0, await provider.IsLockedAsync("resource"));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task TryAcquireAsync_WhenClockIsAdjusted_DoesNotReturnExpiredLeases(int resourceCount, bool moveClockForward)
    {
        var leaseClock = new FakeTimeProvider();
        long initialTimestamp = leaseClock.GetTimestamp();
        var clockAdjustment = TimeSpan.Zero;
        var hostClock = new Mock<TimeProvider>();
        hostClock.Setup(t => t.GetUtcNow()).Returns(() => leaseClock.GetUtcNow() - clockAdjustment);
        hostClock.Setup(t => t.GetTimestamp()).Returns(() => moveClockForward ? initialTimestamp : leaseClock.GetTimestamp());
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
                    clockAdjustment = moveClockForward ? TimeSpan.Zero : TimeSpan.FromMinutes(2);
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

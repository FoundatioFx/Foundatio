using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Lock;
using Foundatio.Messaging;
using Foundatio.Utility;
using Foundatio.Xunit;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Foundatio.Tests.Locks;

public class LockProviderExtensionsTests(ITestOutputHelper output) : TestWithLoggingBase(output)
{
    [Fact]
    public async Task TryAcquireAsync_WhenEarlierLockLost_ReleasesAcquiredLocksAndReturnsNull()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = new InMemoryCacheClient(o => o.TimeProvider(timeProvider).LoggerFactory(Log));
        using var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        var inner = new CacheLockProvider(cache, messageBus, Log);
        ILock? otherOwner = null;
        var locker = new BeforeAcquireLockProvider(inner, timeProvider, async resource =>
        {
            // While "b" is being acquired, "a" expires and another owner takes it, so renewing "a" must fail
            if (!String.Equals(resource, "b", StringComparison.Ordinal))
                return;

            timeProvider.Advance(TimeSpan.FromMinutes(2));
            otherOwner = await inner.AcquireAsync("a", TimeSpan.FromMinutes(10), cancellationToken: TestCancellationToken);
        });

        try
        {
            // Act
            await using var lockInstance = await locker.TryAcquireAsync(["a", "b"], TimeSpan.FromMinutes(1), cancellationToken: TestCancellationToken);

            // Assert
            Assert.Null(lockInstance);
            Assert.False(await inner.IsLockedAsync("b"));
            Assert.NotNull(otherOwner);
            Assert.True(await inner.IsLockedAsync("a"));
            await otherOwner.RenewAsync();
        }
        finally
        {
            if (otherOwner is not null)
                await otherOwner.DisposeAsync();
        }
    }

    private sealed class BeforeAcquireLockProvider(ILockProvider inner, TimeProvider timeProvider, Func<string, Task> beforeAcquire) : ILockProvider, IHaveTimeProvider
    {
        public TimeProvider TimeProvider => timeProvider;

        public async Task<ILock> AcquireAsync(string resource, TimeSpan? timeUntilExpires = null, bool releaseOnDispose = true, CancellationToken cancellationToken = default)
        {
            await beforeAcquire(resource);
            return await inner.AcquireAsync(resource, timeUntilExpires, releaseOnDispose, cancellationToken);
        }

        public async Task<ILock?> TryAcquireAsync(string resource, TimeSpan? timeUntilExpires = null, bool releaseOnDispose = true, CancellationToken cancellationToken = default)
        {
            await beforeAcquire(resource);
            return await inner.TryAcquireAsync(resource, timeUntilExpires, releaseOnDispose, cancellationToken);
        }

        public Task<bool> IsLockedAsync(string resource) => inner.IsLockedAsync(resource);

        public Task ReleaseAsync(string resource, string lockId) => inner.ReleaseAsync(resource, lockId);

        public Task ReleaseAsync(string resource) => inner.ReleaseAsync(resource);

        public Task RenewAsync(string resource, string lockId, TimeSpan? timeUntilExpires = null) => inner.RenewAsync(resource, lockId, timeUntilExpires);
    }
}

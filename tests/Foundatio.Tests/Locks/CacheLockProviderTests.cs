using System;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Lock;
using Foundatio.Messaging;
using Foundatio.Xunit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Foundatio.Tests.Locks;

public class CacheLockProviderTests(ITestOutputHelper output) : TestWithLoggingBase(output)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AcquireAsync_WhenCacheResponseOutlastsLease_DoesNotReturnExpiredHandle(bool tryAcquire, bool replaced)
    {
        // Arrange
        var clock = new FakeTimeProvider();
        using var cache = new DelayedWriteCache(clock, Log);
        using var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        var provider = new CacheLockProvider(cache, messageBus, Log);
        var duration = TimeSpan.FromMinutes(1);
        cache.AfterWriteAsync = async key =>
        {
            clock.Advance(duration + TimeSpan.FromSeconds(1));
            if (replaced)
                await cache.AddAsync(key, "replacement", TimeSpan.FromMinutes(10));
        };

        // Act & Assert
        if (tryAcquire)
        {
            await using var lease = await provider.TryAcquireAsync("resource", duration, cancellationToken: TestCancellationToken);
            Assert.Null(lease);
        }
        else
        {
            await Assert.ThrowsAsync<LockAcquisitionTimeoutException>(() => provider.AcquireAsync("resource", duration, cancellationToken: TestCancellationToken));
        }

        var owner = await cache.GetAsync<string>("lock:resource");
        Assert.Equal(replaced, owner.HasValue);
        if (replaced)
            Assert.Equal("replacement", owner.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task AcquireAsync_WithSupportedShortDuration_PreservesExpirationSemantics(int milliseconds)
    {
        // Arrange
        var clock = new FakeTimeProvider();
        using var cache = new InMemoryCacheClient(o => o.TimeProvider(clock).LoggerFactory(Log));
        using var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        var provider = new CacheLockProvider(cache, messageBus, Log);

        // Act
        await using var acquired = await provider.AcquireAsync("resource", TimeSpan.FromMilliseconds(milliseconds), cancellationToken: TestCancellationToken);

        // Assert
        Assert.True(await provider.IsLockedAsync("resource"));
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(milliseconds == 0, await provider.IsLockedAsync("resource"));
    }

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(11, true)]
    public async Task RenewAsync_WhenCacheResponseIsDelayed_ChecksRemainingLease(int elapsedSeconds, bool expired)
    {
        // Arrange
        var clock = new FakeTimeProvider();
        using var cache = new DelayedWriteCache(clock, Log);
        using var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        var provider = new CacheLockProvider(cache, messageBus, Log);
        var duration = TimeSpan.FromSeconds(10);
        await using var lease = await provider.AcquireAsync("resource", duration, cancellationToken: TestCancellationToken);
        cache.AfterWriteAsync = _ =>
        {
            clock.Advance(TimeSpan.FromSeconds(elapsedSeconds));
            return Task.CompletedTask;
        };

        // Act
        var renewal = lease.RenewAsync(duration);

        // Assert
        if (expired)
            await Assert.ThrowsAsync<LockException>(() => renewal);
        else
            await renewal;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenewAsync_WhenLeaseExpired_ThrowsLockException(bool acquiredByAnotherOwner)
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = new InMemoryCacheClient(o => o.TimeProvider(timeProvider).LoggerFactory(Log));
        using var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        var locker = new CacheLockProvider(cache, messageBus, Log);
        await using var staleLock = await locker.AcquireAsync("resource", TimeSpan.FromMinutes(1), cancellationToken: TestCancellationToken);
        timeProvider.Advance(TimeSpan.FromMinutes(2));
        await using var newLock = acquiredByAnotherOwner
            ? await locker.AcquireAsync("resource", TimeSpan.FromMinutes(10), cancellationToken: TestCancellationToken)
            : null;

        // Act
        await Assert.ThrowsAsync<LockException>(() => staleLock.RenewAsync());

        // Assert
        Assert.Equal(0, staleLock.RenewalCount);
        Assert.Equal(acquiredByAnotherOwner, await locker.IsLockedAsync("resource"));
        if (newLock is not null)
        {
            await staleLock.ReleaseAsync();
            Assert.True(await locker.IsLockedAsync("resource"));
            await newLock.RenewAsync();
        }
    }

    [Fact]
    public async Task RenewAsync_WithCurrentOwner_ExtendsLease()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = new InMemoryCacheClient(o => o.TimeProvider(timeProvider).LoggerFactory(Log));
        using var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        var locker = new CacheLockProvider(cache, messageBus, Log);
        await using var lockInstance = await locker.AcquireAsync("resource", TimeSpan.FromMinutes(1), cancellationToken: TestCancellationToken);
        timeProvider.Advance(TimeSpan.FromSeconds(30));

        // Act
        await lockInstance.RenewAsync(TimeSpan.FromMinutes(3));
        timeProvider.Advance(TimeSpan.FromMinutes(1));

        // Assert
        Assert.True(await locker.IsLockedAsync("resource"));
        Assert.Equal(1, lockInstance.RenewalCount);
    }

    [Fact]
    public async Task RenewAsync_WithMinimumDuration_RenewsCurrentOwner()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = new InMemoryCacheClient(o => o.TimeProvider(timeProvider).LoggerFactory(Log));
        using var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        var locker = new CacheLockProvider(cache, messageBus, Log);
        await using var lease = await locker.AcquireAsync("resource", TimeSpan.FromMinutes(1), cancellationToken: TestCancellationToken);

        // Act
        await lease.RenewAsync(TimeSpan.FromMilliseconds(5));

        // Assert
        Assert.True(await locker.IsLockedAsync("resource"));
        Assert.Equal(1, lease.RenewalCount);
        timeProvider.Advance(TimeSpan.FromMilliseconds(6));
        Assert.False(await locker.IsLockedAsync("resource"));
    }

    // The cache commits normally. Only delivery of the successful write response is delayed.
    private sealed class DelayedWriteCache(TimeProvider clock, ILoggerFactory loggerFactory)
        : InMemoryCacheClient(o => o.TimeProvider(clock).LoggerFactory(loggerFactory)), ICacheClient
    {
        public Func<string, Task> AfterWriteAsync { get; set; } = _ => Task.CompletedTask;

        async Task<bool> ICacheClient.AddAsync<T>(string key, T value, TimeSpan? expiresIn)
        {
            bool added = await AddAsync(key, value, expiresIn);
            if (added)
                await AfterWriteAsync(key);

            return added;
        }

        async Task<bool> ICacheClient.ReplaceIfEqualAsync<T>(string key, T value, T expected, TimeSpan? expiresIn)
        {
            bool renewed = await ReplaceIfEqualAsync(key, value, expected, expiresIn);
            if (renewed)
                await AfterWriteAsync(key);

            return renewed;
        }
    }
}

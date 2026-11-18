using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Lock;
using Foundatio.Messaging;
using Foundatio.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Foundatio.Tests.Locks;

public class InMemoryLockTests : LockTestBase, IDisposable
{
    private readonly ICacheClient _cache;
    private readonly IMessageBus _messageBus;

    public InMemoryLockTests(ITestOutputHelper output) : base(output)
    {
        _cache = new InMemoryCacheClient(o => o.LoggerFactory(Log));
        _messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
    }

    protected override ILockProvider GetThrottlingLockProvider(int maxHits, TimeSpan period)
    {
        return new ThrottlingLockProvider(_cache, maxHits, period, null, null, Log);
    }

    protected override ILockProvider GetLockProvider()
    {
        return new CacheLockProvider(_cache, _messageBus, null, null, Log);
    }

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
        var provider = new CacheLockProvider(cache, _messageBus, Log);
        var duration = TimeSpan.FromMinutes(1);
        cache.AfterWriteAsync = async key =>
        {
            clock.Advance(duration + TimeSpan.FromSeconds(1));
            if (replaced)
                await cache.AddAsync(key, "replacement", TimeSpan.FromMinutes(10));
        };

        // Act & Assert
        var singleAttempt = new CancellationToken(true);
        if (tryAcquire)
        {
            await using var lease = await provider.TryAcquireAsync("resource", duration, cancellationToken: singleAttempt);
            Assert.Null(lease);
        }
        else
        {
            await Assert.ThrowsAsync<LockAcquisitionTimeoutException>(() => provider.AcquireAsync("resource", duration, cancellationToken: singleAttempt));
        }

        var owner = await cache.GetAsync<string>("lock:resource");
        Assert.Equal(replaced, owner.HasValue);
        if (replaced)
            Assert.Equal("replacement", owner.Value);
    }

    [Fact]
    public async Task AcquireAsync_WhenCacheResponseOutlastsLease_RetriesUntilAcquired()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        using var cache = new DelayedWriteCache(clock, Log);
        var provider = new CacheLockProvider(cache, _messageBus, Log);
        var duration = TimeSpan.FromMinutes(1);
        int writes = 0;
        cache.AfterWriteAsync = _ =>
        {
            if (Interlocked.Increment(ref writes) is 1)
                clock.Advance(duration + TimeSpan.FromSeconds(1));

            return Task.CompletedTask;
        };

        // Act
        await using var lease = await provider.AcquireAsync("resource", duration, cancellationToken: TestCancellationToken);

        // Assert
        Assert.Equal(2, writes);
        var owner = await cache.GetAsync<string>("lock:resource");
        Assert.Equal(lease.LockId, owner.Value);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(-1, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    public override Task AcquireAsync_WithInvalidDuration_PreservesCurrentOwner(int milliseconds, bool tryAcquire)
    {
        return base.AcquireAsync_WithInvalidDuration_PreservesCurrentOwner(milliseconds, tryAcquire);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task AcquireAsync_WithSupportedShortDuration_PreservesExpirationSemantics(int milliseconds)
    {
        // Arrange
        var clock = new FakeTimeProvider();
        using var cache = new InMemoryCacheClient(o => o.TimeProvider(clock).LoggerFactory(Log));
        var provider = new CacheLockProvider(cache, _messageBus, Log);

        // Act
        await using var acquired = await provider.AcquireAsync("resource", TimeSpan.FromMilliseconds(milliseconds), cancellationToken: TestCancellationToken);

        // Assert
        Assert.True(await provider.IsLockedAsync("resource"));
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(milliseconds is 0, await provider.IsLockedAsync("resource"));
    }

    [Fact]
    public override Task CanAcquireAndReleaseLockAsync()
    {
        return base.CanAcquireAndReleaseLockAsync();
    }

    [Fact]
    public override Task LockWillTimeoutAsync()
    {
        return base.LockWillTimeoutAsync();
    }

    [Fact]
    public override Task LockWontTimeoutEarly()
    {
        return base.LockWontTimeoutEarly();
    }

    [Fact]
    public override Task LockOneAtATimeAsync()
    {
        return base.LockOneAtATimeAsync();
    }

    [Fact]
    public override Task CanAcquireMultipleResources()
    {
        return base.CanAcquireMultipleResources();
    }

    [Fact]
    public override Task CanAcquireLocksInParallel()
    {
        return base.CanAcquireLocksInParallel();
    }

    [Fact]
    public override Task CanAcquireScopedLocksInParallel()
    {
        return base.CanAcquireScopedLocksInParallel();
    }

    [Fact]
    public override Task CanAcquireMultipleLocksInParallel()
    {
        return base.CanAcquireMultipleLocksInParallel();
    }

    [Fact]
    public override Task CanAcquireMultipleScopedResources()
    {
        return base.CanAcquireMultipleScopedResources();
    }

    [Fact]
    public override Task RenewAsync_AfterRelease_ThrowsLockExceptionAndDoesNotRecreateLock()
    {
        return base.RenewAsync_AfterRelease_ThrowsLockExceptionAndDoesNotRecreateLock();
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
        var provider = new CacheLockProvider(cache, _messageBus, Log);
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

    [Fact]
    public async Task RenewAsync_WhenCacheThrows_ThrowsLockExceptionWithInnerException()
    {
        // Arrange
        using var cache = new DelayedWriteCache(TimeProvider.System, Log);
        var resiliencePolicyProvider = new ResiliencePolicyProvider().WithDefaultPolicy(p => p.WithMaxAttempts(1));
        var locker = new CacheLockProvider(cache, _messageBus, null, resiliencePolicyProvider, Log);
        await using var lease = await locker.AcquireAsync("resource", TimeSpan.FromMinutes(1), cancellationToken: TestCancellationToken);
        var cacheError = new TimeoutException("cache unavailable");
        cache.AfterWriteAsync = _ => throw cacheError;

        // Act
        var ex = await Assert.ThrowsAsync<LockException>(() => lease.RenewAsync());

        // Assert
        Assert.Same(cacheError, ex.InnerException);
        Assert.Equal(0, lease.RenewalCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenewAsync_WhenLeaseExpired_ThrowsLockException(bool acquiredByAnotherOwner)
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = new InMemoryCacheClient(o => o.TimeProvider(timeProvider).LoggerFactory(Log));
        var locker = new CacheLockProvider(cache, _messageBus, Log);
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
        var locker = new CacheLockProvider(cache, _messageBus, Log);
        await using var lockInstance = await locker.AcquireAsync("resource", TimeSpan.FromMinutes(1), cancellationToken: TestCancellationToken);
        timeProvider.Advance(TimeSpan.FromSeconds(30));

        // Act
        await lockInstance.RenewAsync(TimeSpan.FromMinutes(3));
        timeProvider.Advance(TimeSpan.FromMinutes(1));

        // Assert
        Assert.True(await locker.IsLockedAsync("resource"));
        Assert.Equal(1, lockInstance.RenewalCount);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(4)]
    public override Task RenewAsync_WithInvalidDuration_PreservesCurrentOwner(int milliseconds)
    {
        return base.RenewAsync_WithInvalidDuration_PreservesCurrentOwner(milliseconds);
    }

    [Fact]
    public async Task RenewAsync_WithMinimumDuration_RenewsCurrentOwner()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = new InMemoryCacheClient(o => o.TimeProvider(timeProvider).LoggerFactory(Log));
        var locker = new CacheLockProvider(cache, _messageBus, Log);
        await using var lease = await locker.AcquireAsync("resource", TimeSpan.FromMinutes(1), cancellationToken: TestCancellationToken);

        // Act
        await lease.RenewAsync(TimeSpan.FromMilliseconds(5));

        // Assert
        Assert.True(await locker.IsLockedAsync("resource"));
        Assert.Equal(1, lease.RenewalCount);
        timeProvider.Advance(TimeSpan.FromMilliseconds(6));
        Assert.False(await locker.IsLockedAsync("resource"));
    }

    [Fact]
    public override Task RenewAsync_WithMissingLock_ThrowsLockException()
    {
        return base.RenewAsync_WithMissingLock_ThrowsLockException();
    }

    [Fact]
    public override Task RenewAsync_WithMultipleResources_WhenOneLockReplaced_ThrowsLockExceptionAndPreservesCurrentOwner()
    {
        return base.RenewAsync_WithMultipleResources_WhenOneLockReplaced_ThrowsLockExceptionAndPreservesCurrentOwner();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public override Task RenewAsync_WithReplacedOwner_ThrowsLockExceptionAndPreservesCurrentOwner(bool scoped)
    {
        return base.RenewAsync_WithReplacedOwner_ThrowsLockExceptionAndPreservesCurrentOwner(scoped);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public override Task TryAcquireAsync_WithMultipleResources_WhenResourceNamesShareSuffix_ReleasesAcquiredLocksAndReturnsNull(bool scoped)
    {
        return base.TryAcquireAsync_WithMultipleResources_WhenResourceNamesShareSuffix_ReleasesAcquiredLocksAndReturnsNull(scoped);
    }

    [Fact]
    public override Task WillThrottleCallsAsync()
    {
        return base.WillThrottleCallsAsync();
    }

    [Fact]
    public override Task AcquireAsync_AfterPeriodExhausted_RecoversWithinNextPeriodAsync()
    {
        return base.AcquireAsync_AfterPeriodExhausted_RecoversWithinNextPeriodAsync();
    }

    [Fact]
    public override Task AcquireAsync_ThrowsWhenLockNotAvailableAsync()
    {
        return base.AcquireAsync_ThrowsWhenLockNotAvailableAsync();
    }

    [Fact]
    public override Task AcquireAsync_ThrowsWhenCancellationTokenCancelledAsync()
    {
        return base.AcquireAsync_ThrowsWhenCancellationTokenCancelledAsync();
    }

    [Fact]
    public override Task AcquireAsync_MultiResource_ThrowsWhenAnyLockUnavailableAsync()
    {
        return base.AcquireAsync_MultiResource_ThrowsWhenAnyLockUnavailableAsync();
    }

    [Fact]
    public override Task CanReleaseLockMultipleTimes()
    {
        return base.CanReleaseLockMultipleTimes();
    }

    [Fact]
    public override Task AcquireAsync_WithReleaseOnDisposeFalse_DoesNotReleaseOnDispose()
    {
        return base.AcquireAsync_WithReleaseOnDisposeFalse_DoesNotReleaseOnDispose();
    }

    [Fact]
    public override Task Lock_AcquiredTimeUtc_ReturnsValidTimestamp()
    {
        return base.Lock_AcquiredTimeUtc_ReturnsValidTimestamp();
    }

    [Fact]
    public override Task Lock_LockIdAndResource_ReturnCorrectValues()
    {
        return base.Lock_LockIdAndResource_ReturnCorrectValues();
    }

    [Fact]
    public override Task ReleaseAsync_WithForceRelease_ReleasesLockWithoutLockId()
    {
        return base.ReleaseAsync_WithForceRelease_ReleasesLockWithoutLockId();
    }

    [Fact]
    public async Task TryAcquireAsync_WithExhaustedPeriod_WaitsForNextPeriod()
    {
        // Arrange
        const int allowedLocks = 25;
        var period = TimeSpan.FromSeconds(2);
        // A fixed start aligned to the throttling period, so the boundary below is deterministic
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var cache = new InMemoryCacheClient(o => o.TimeProvider(timeProvider).LoggerFactory(Log));
        var locker = new ThrottlingLockProvider(cache, allowedLocks, period, timeProvider, null, Log);
        for (int i = 0; i < allowedLocks; i++)
        {
            await using var hit = await locker.TryAcquireAsync("resource", cancellationToken: TestCancellationToken);
            Assert.NotNull(hit);
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestCancellationToken);

        // Act
        var waiting = locker.TryAcquireAsync("resource", cancellationToken: cancellation.Token);
        try
        {
            bool waitsForQuota = !waiting.IsCompleted;
            timeProvider.Advance(period - TimeSpan.FromMilliseconds(1));
            bool waitsBeforeBoundary = !waiting.IsCompleted;
            bool exhaustedBeforeBoundary = await locker.IsLockedAsync("resource");
            timeProvider.Advance(TimeSpan.FromMilliseconds(1));
            bool exhaustedAtBoundary = await locker.IsLockedAsync("resource");
            // The provider wakes one millisecond after the boundary to avoid early system-timer wakeups.
            timeProvider.Advance(TimeSpan.FromMilliseconds(1));
            await using var nextPeriodHit = await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);

            // Assert
            Assert.True(waitsForQuota);
            Assert.True(waitsBeforeBoundary);
            Assert.True(exhaustedBeforeBoundary);
            Assert.False(exhaustedAtBoundary);
            Assert.NotNull(nextPeriodHit);
        }
        finally
        {
            await cancellation.CancelAsync();
        }
    }

    [Fact]
    public override Task TryUsingAsync_WithSuccessfulAction_ExecutesAndReleasesLock()
    {
        return base.TryUsingAsync_WithSuccessfulAction_ExecutesAndReleasesLock();
    }

    public void Dispose()
    {
        _cache.Dispose();
        _messageBus.Dispose();
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

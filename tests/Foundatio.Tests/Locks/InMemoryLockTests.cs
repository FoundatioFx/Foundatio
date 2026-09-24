using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Lock;
using Foundatio.Messaging;
using Foundatio.Utility;
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenewAsync_WhenLeaseExpired_ThrowsLockException(bool acquiredByAnotherOwner)
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = new InMemoryCacheClient(o => o.TimeProvider(timeProvider).LoggerFactory(Log));
        var locker = new CacheLockProvider(cache, null, timeProvider, null, Log);
        await using var staleLock = await locker.AcquireAsync("resource", TimeSpan.FromMinutes(1), cancellationToken: TestContext.Current.CancellationToken);
        timeProvider.Advance(TimeSpan.FromMinutes(2));
        await using var newLock = acquiredByAnotherOwner
            ? await locker.AcquireAsync("resource", TimeSpan.FromMinutes(10), cancellationToken: TestContext.Current.CancellationToken)
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
        var locker = new CacheLockProvider(cache, null, timeProvider, null, Log);
        await using var lockInstance = await locker.AcquireAsync("resource", TimeSpan.FromMinutes(1), cancellationToken: TestContext.Current.CancellationToken);
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
    public async Task RenewAsync_WithInvalidDuration_PreservesCurrentOwner(int milliseconds)
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = new InMemoryCacheClient(o => o.TimeProvider(timeProvider).LoggerFactory(Log));
        var locker = new CacheLockProvider(cache, null, timeProvider, null, Log);
        await using var stale = await locker.AcquireAsync("resource", TimeSpan.FromMinutes(1), cancellationToken: TestCancellationToken);
        timeProvider.Advance(TimeSpan.FromMinutes(2));
        await using var current = await locker.AcquireAsync("resource", TimeSpan.FromMinutes(10), cancellationToken: TestCancellationToken);

        // Act
        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => stale.RenewAsync(TimeSpan.FromMilliseconds(milliseconds)));

        // Assert
        Assert.Equal("timeUntilExpires", error.ParamName);
        Assert.Equal(0, stale.RenewalCount);
        Assert.True(await locker.IsLockedAsync("resource"));
        await current.RenewAsync();
        Assert.Equal(1, current.RenewalCount);
    }

    [Fact]
    public async Task TryAcquireAsync_WithMultipleResources_WhenEarlierLockLost_ReleasesAcquiredLocksAndReturnsNull()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = new InMemoryCacheClient(o => o.TimeProvider(timeProvider).LoggerFactory(Log));
        var inner = new CacheLockProvider(cache, null, timeProvider, null, Log);
        ILock? otherOwner = null;
        var locker = new BeforeAcquireLockProvider(inner, timeProvider, async resource =>
        {
            // While "b" is being acquired, "a" expires and another owner takes it, so renewing "a" must fail
            if (resource != "b")
                return;

            timeProvider.Advance(TimeSpan.FromMinutes(2));
            otherOwner = await inner.AcquireAsync("a", TimeSpan.FromMinutes(10), cancellationToken: TestContext.Current.CancellationToken);
        });

        try
        {
            // Act
            await using var lockInstance = await locker.TryAcquireAsync(["a", "b"], TimeSpan.FromMinutes(1), cancellationToken: TestContext.Current.CancellationToken);

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

    [Fact]
    public override async Task WillThrottleCallsAsync()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var cache = new InMemoryCacheClient(o => o.TimeProvider(timeProvider).LoggerFactory(Log));
        var period = TimeSpan.FromSeconds(2);
        var locker = new ThrottlingLockProvider(cache, 25, period, timeProvider, null, Log);
        for (int i = 0; i < 25; i++)
        {
            await using var hit = await locker.TryAcquireAsync("resource", cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(hit);
        }

        // Act
        var exhausted = await locker.TryAcquireAsync("resource", cancellationToken: new CancellationToken(true));
        timeProvider.Advance(period);
        await using var nextPeriodHit = await locker.TryAcquireAsync("resource", cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(exhausted);
        Assert.NotNull(nextPeriodHit);
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
    public override Task TryUsingAsync_WithSuccessfulAction_ExecutesAndReleasesLock()
    {
        return base.TryUsingAsync_WithSuccessfulAction_ExecutesAndReleasesLock();
    }


    public void Dispose()
    {
        _cache.Dispose();
        _messageBus.Dispose();
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

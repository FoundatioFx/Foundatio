using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Lock;
using Foundatio.Messaging;
using Foundatio.Utility;
using Microsoft.Extensions.Time.Testing;
using Moq;
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
    public override Task RenewAsync_WithInvalidDuration_PreservesCurrentOwner(int milliseconds)
    {
        return base.RenewAsync_WithInvalidDuration_PreservesCurrentOwner(milliseconds);
    }

    [Fact]
    public async Task RenewAsync_WithMinimumDuration_RenewsCurrentOwner()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = new InMemoryCacheClient(o => o.LoggerFactory(Log).TimeProvider(timeProvider));
        var locker = new CacheLockProvider(cache, null, timeProvider, null, Log);
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
    public async Task RenewAsync_WithMultipleResources_WhenFirstLockThrowsSynchronously_AttemptsEveryRenewal()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var error = new CacheException("Provider unavailable");
        var first = new Mock<ILock>();
        first.Setup(l => l.RenewAsync(It.IsAny<TimeSpan?>())).Throws(error);
        var second = new Mock<ILock>();
        second.Setup(l => l.RenewAsync(It.IsAny<TimeSpan?>())).Returns(Task.CompletedTask);
        var locker = CreateMockLockProvider(timeProvider, ("a", first.Object), ("b", second.Object));
        await using var lockInstance = await locker.Object.TryAcquireAsync(["a", "b"], TimeSpan.FromMinutes(1), cancellationToken: TestCancellationToken);
        Assert.NotNull(lockInstance);

        // Act
        var thrown = await Assert.ThrowsAsync<CacheException>(() => lockInstance.RenewAsync());

        // Assert
        Assert.Same(error, thrown);
        Assert.Equal(0, lockInstance.RenewalCount);
        second.Verify(l => l.RenewAsync(It.IsAny<TimeSpan?>()), Times.Once);
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

    [Fact]
    public async Task TryAcquireAsync_WithMultipleResources_AfterRenewal_WaitsUntilNextRenewalInterval()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var cache = new InMemoryCacheClient(o => o.TimeProvider(timeProvider).LoggerFactory(Log));
        var inner = new CacheLockProvider(cache, null, timeProvider, null, Log);
        var locker = new BeforeAcquireLockProvider(inner, timeProvider, resource =>
        {
            if (resource == "b")
                timeProvider.Advance(TimeSpan.FromSeconds(31));
            else if (resource == "c")
                timeProvider.Advance(TimeSpan.FromSeconds(1));
            return Task.CompletedTask;
        });

        // Act
        await using var lockInstance = await locker.TryAcquireAsync(["a", "b", "c"], TimeSpan.FromMinutes(1), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(lockInstance);
        Assert.Equal(TimeSpan.FromSeconds(59), await cache.GetExpirationAsync("lock:a"));
        Assert.True(await inner.IsLockedAsync("a"));
        Assert.True(await inner.IsLockedAsync("b"));
        Assert.True(await inner.IsLockedAsync("c"));
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TryAcquireAsync_WithMultipleResources_WhenProviderThrows_ReleasesAcquiredLocks(bool duringRenewal, bool releaseThrows)
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var first = new Mock<ILock>();
        var second = new Mock<ILock>();
        first.Setup(l => l.ReleaseAsync()).Returns(() => releaseThrows
            ? throw new CacheException("Release unavailable")
            : Task.CompletedTask);
        second.Setup(l => l.ReleaseAsync()).Returns(Task.CompletedTask);
        var error = new CacheException("Provider unavailable");
        first.Setup(l => l.RenewAsync(It.IsAny<TimeSpan?>())).ThrowsAsync(error);
        var locker = new Mock<ILockProvider>();
        locker.As<IHaveTimeProvider>().SetupGet(p => p.TimeProvider).Returns(timeProvider);
        locker.Setup(p => p.TryAcquireAsync("a", It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(first.Object);
        locker.Setup(p => p.TryAcquireAsync("b", It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(() =>
        {
            timeProvider.Advance(TimeSpan.FromSeconds(31));
            return duringRenewal ? Task.FromResult<ILock?>(second.Object) : Task.FromException<ILock?>(error);
        });

        // Act
        var thrown = await Assert.ThrowsAsync<CacheException>(() => locker.Object.TryAcquireAsync(["a", "b"], TimeSpan.FromMinutes(1), cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.Same(error, thrown);
        first.Verify(l => l.ReleaseAsync(), Times.Once);
        second.Verify(l => l.ReleaseAsync(), duringRenewal ? Times.Once() : Times.Never());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public override Task TryAcquireAsync_WithMultipleResources_WhenResourceNamesShareSuffix_ReleasesAcquiredLocksAndReturnsNull(bool scoped)
    {
        return base.TryAcquireAsync_WithMultipleResources_WhenResourceNamesShareSuffix_ReleasesAcquiredLocksAndReturnsNull(scoped);
    }

    [Fact]
    public async Task TryAcquireAsync_WithMultipleResources_WhenRenewalLosesLockAndProviderThrows_ReturnsNullAndReleasesAcquiredLocks()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var providerError = new CacheException("Provider unavailable");
        var lost = new Mock<ILock>();
        lost.Setup(l => l.RenewAsync(It.IsAny<TimeSpan?>())).ThrowsAsync(new LockException("Lock lost"));
        lost.Setup(l => l.ReleaseAsync()).Returns(Task.CompletedTask);
        var failing = new Mock<ILock>();
        failing.Setup(l => l.RenewAsync(It.IsAny<TimeSpan?>())).ThrowsAsync(providerError);
        failing.Setup(l => l.ReleaseAsync()).Returns(Task.CompletedTask);
        var last = new Mock<ILock>();
        last.Setup(l => l.ReleaseAsync()).Returns(Task.CompletedTask);
        var locker = CreateMockLockProvider(timeProvider, ("a", lost.Object), ("b", failing.Object), ("c", last.Object));
        locker.Setup(p => p.TryAcquireAsync("c", It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(() =>
        {
            timeProvider.Advance(TimeSpan.FromSeconds(31));
            return Task.FromResult<ILock?>(last.Object);
        });

        // Act
        var lockInstance = await locker.Object.TryAcquireAsync(["a", "b", "c"], TimeSpan.FromMinutes(1), cancellationToken: TestCancellationToken);

        // Assert
        Assert.Null(lockInstance);
        lost.Verify(l => l.ReleaseAsync(), Times.Once);
        failing.Verify(l => l.ReleaseAsync(), Times.Once);
        last.Verify(l => l.ReleaseAsync(), Times.Once);
    }

    [Fact]
    public async Task TryAcquireAsync_WithMultipleResources_WhenUnavailableAndReleaseThrows_ReturnsNullAndAttemptsEveryRelease()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var first = new Mock<ILock>();
        first.Setup(l => l.ReleaseAsync()).Throws(new CacheException("Release unavailable"));
        var second = new Mock<ILock>();
        second.Setup(l => l.ReleaseAsync()).Returns(Task.CompletedTask);
        var locker = CreateMockLockProvider(timeProvider, ("a", first.Object), ("b", second.Object), ("c", null));

        // Act
        var lockInstance = await locker.Object.TryAcquireAsync(["a", "b", "c"], TimeSpan.FromMinutes(1), cancellationToken: TestCancellationToken);

        // Assert
        Assert.Null(lockInstance);
        first.Verify(l => l.ReleaseAsync(), Times.Once);
        second.Verify(l => l.ReleaseAsync(), Times.Once);
    }

    public void Dispose()
    {
        _cache.Dispose();
        _messageBus.Dispose();
    }

    private static Mock<ILockProvider> CreateMockLockProvider(TimeProvider timeProvider, params (string Resource, ILock? Lock)[] locks)
    {
        var locker = new Mock<ILockProvider>();
        locker.As<IHaveTimeProvider>().SetupGet(p => p.TimeProvider).Returns(timeProvider);
        foreach (var (resource, lockInstance) in locks)
            locker.Setup(p => p.TryAcquireAsync(resource, It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(lockInstance);

        return locker;
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

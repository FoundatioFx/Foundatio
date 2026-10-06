using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Lock;
using Foundatio.Messaging;
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
}

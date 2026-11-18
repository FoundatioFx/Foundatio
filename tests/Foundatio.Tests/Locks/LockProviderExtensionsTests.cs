using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Lock;
using Foundatio.Messaging;
using Foundatio.Utility;
using Foundatio.Xunit;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Foundatio.Tests.Locks;

public class LockProviderExtensionsTests(ITestOutputHelper output) : TestWithLoggingBase(output)
{
    [Theory]
    [InlineData(1, 59)]
    [InlineData(1, 61)]
    [InlineData(2, 59)]
    [InlineData(2, 61)]
    public async Task TryAcquireAsync_AfterSlowAcquisition_DoesNotReturnExpiredLease(int resourceCount, int acquisitionSeconds)
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var cache = new InMemoryCacheClient(o => o.TimeProvider(timeProvider).LoggerFactory(Log));
        using var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        var inner = new CacheLockProvider(cache, messageBus, Log);
        var locker = CreateMockLockProvider(timeProvider);
        var resources = Enumerable.Range(0, resourceCount).Select(i => i.ToString()).ToArray();
        locker.Setup(p => p.TryAcquireAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(async (string resource, TimeSpan? duration, bool release, CancellationToken token) =>
            {
                var result = await inner.TryAcquireAsync(resource, duration, release, token);
                if (String.Equals(resource, resources[^1], StringComparison.Ordinal))
                    timeProvider.Advance(TimeSpan.FromSeconds(acquisitionSeconds));
                return result;
            });

        // Act
        await using var acquired = await locker.Object.TryAcquireAsync(resources, TimeSpan.FromMinutes(1), cancellationToken: TestCancellationToken);

        // Assert
        if (acquisitionSeconds < 60)
            Assert.NotNull(acquired);
        else
            Assert.Null(acquired);
        foreach (string resource in resources)
            Assert.Equal(acquisitionSeconds < 60, await inner.IsLockedAsync(resource));
    }

    [Theory]
    [InlineData(59, false, false)]
    [InlineData(60, false, false)]
    [InlineData(61, true, false)]
    [InlineData(61, false, true)]
    [InlineData(1200, false, true)]
    public async Task TryAcquireAsync_AfterSlowRenewal_ReturnsOnlyUnexpiredLeases(int renewalSeconds, bool acquiredByAnotherOwner, bool useDefaultDuration)
    {
        // Arrange
        TimeSpan? leaseDuration = useDefaultDuration ? null : TimeSpan.FromMinutes(1);
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var cache = new InMemoryCacheClient(o => o.TimeProvider(timeProvider).LoggerFactory(Log));
        using var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        var inner = new CacheLockProvider(cache, messageBus, Log);
        var renewalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishRenewal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new Mock<ILock>();
        ILock? firstLease = null;
        first.SetupGet(l => l.Resource).Returns("a");
        first.Setup(l => l.ReleaseAsync()).Returns(() => firstLease!.ReleaseAsync());
        first.Setup(l => l.RenewAsync(It.IsAny<TimeSpan?>())).Returns(async (TimeSpan? extension) =>
        {
            await firstLease!.RenewAsync(extension);
            renewalStarted.SetResult();
            await finishRenewal.Task;
        });
        var locker = CreateMockLockProvider(timeProvider);
        locker.Setup(p => p.TryAcquireAsync("a", It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(async () =>
        {
            firstLease = await inner.TryAcquireAsync("a", leaseDuration, cancellationToken: TestCancellationToken);
            return first.Object;
        });
        locker.Setup(p => p.TryAcquireAsync("b", It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(async () =>
        {
            timeProvider.Advance(TimeSpan.FromSeconds(31));
            return await inner.TryAcquireAsync("b", leaseDuration, cancellationToken: TestCancellationToken);
        });

        // Act
        var acquisition = locker.Object.TryAcquireAsync(["a", "b"], leaseDuration, cancellationToken: TestCancellationToken);
        try
        {
            await renewalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);
            timeProvider.Advance(TimeSpan.FromSeconds(renewalSeconds));
            await using var newOwner = acquiredByAnotherOwner
                ? await inner.AcquireAsync("b", TimeSpan.FromMinutes(10), cancellationToken: TestCancellationToken)
                : null;
            finishRenewal.SetResult();
            await using var lockInstance = await acquisition.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);

            // Assert
            if (TimeSpan.FromSeconds(renewalSeconds) < (leaseDuration ?? TimeSpan.FromMinutes(20)))
            {
                Assert.NotNull(lockInstance);
                Assert.True(await inner.IsLockedAsync("a"));
                Assert.True(await inner.IsLockedAsync("b"));
                first.Verify(l => l.ReleaseAsync(), Times.Never);
            }
            else
            {
                Assert.Null(lockInstance);
                Assert.False(await inner.IsLockedAsync("a"));
                Assert.Equal(acquiredByAnotherOwner, await inner.IsLockedAsync("b"));
                first.Verify(l => l.ReleaseAsync(), Times.Once);
            }

            if (newOwner is not null)
                await newOwner.RenewAsync();
        }
        finally
        {
            finishRenewal.TrySetResult();
            await using var remaining = await acquisition.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);
        }
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task TryAcquireAsync_WhenClockIsAdjusted_DoesNotReturnExpiredLeases(int resourceCount, bool moveClockForward)
    {
        // Arrange
        var leaseClock = new FakeTimeProvider();
        long initialTimestamp = leaseClock.GetTimestamp();
        var clockAdjustment = TimeSpan.Zero;
        var hostClock = new Mock<TimeProvider>();
        hostClock.Setup(t => t.GetUtcNow()).Returns(() => leaseClock.GetUtcNow() - clockAdjustment);
        hostClock.Setup(t => t.GetTimestamp()).Returns(() => moveClockForward ? initialTimestamp : leaseClock.GetTimestamp());
        hostClock.SetupGet(t => t.TimestampFrequency).Returns(leaseClock.TimestampFrequency);

        using var cache = new InMemoryCacheClient(o => o.TimeProvider(leaseClock).LoggerFactory(Log));
        using var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        var inner = new CacheLockProvider(cache, messageBus, Log);
        var provider = new Mock<ILockProvider>();
        provider.As<IHaveTimeProvider>().SetupGet(p => p.TimeProvider).Returns(hostClock.Object);
        string[] resources = Enumerable.Range(0, resourceCount).Select(i => i.ToString()).ToArray();
        ILock? replacement = null;
        provider.Setup(p => p.TryAcquireAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(async (string resource, TimeSpan? duration, bool release, CancellationToken token) =>
            {
                var acquired = await inner.TryAcquireAsync(resource, duration, release, token);
                if (String.Equals(resource, resources[^1], StringComparison.Ordinal))
                {
                    leaseClock.Advance(TimeSpan.FromSeconds(61));
                    clockAdjustment = moveClockForward ? TimeSpan.Zero : TimeSpan.FromMinutes(2);
                    replacement = await inner.AcquireAsync(resources[0], TimeSpan.FromMinutes(10), cancellationToken: token);
                }

                return acquired;
            });

        try
        {
            // Act
            await using var acquired = await provider.Object.TryAcquireAsync(resources, TimeSpan.FromMinutes(1), cancellationToken: TestCancellationToken);

            // Assert
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

    [Theory]
    [InlineData("en-US")]
    [InlineData("sv-SE")]
    public async Task TryAcquireAsync_WithDifferentCultures_UsesConsistentResourceOrder(string culture)
    {
        // Arrange
        using var cache = new InMemoryCacheClient(o => o.LoggerFactory(Log));
        using var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        var provider = new CacheLockProvider(cache, messageBus, Log);
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);

            // Act
            await using var lease = await provider.TryAcquireAsync(["ä", "z", "a", "ä"], TimeSpan.Zero, cancellationToken: TestCancellationToken);

            // Assert
            Assert.NotNull(lease);
            Assert.Equal("a+z+ä", lease.Resource);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public async Task TryAcquireAsync_WithMultipleResources_AfterRenewal_WaitsUntilNextRenewalInterval()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var cache = new InMemoryCacheClient(o => o.TimeProvider(timeProvider).LoggerFactory(Log));
        using var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        var inner = new CacheLockProvider(cache, messageBus, Log);
        var locker = new BeforeAcquireLockProvider(inner, timeProvider, resource =>
        {
            if (String.Equals(resource, "b", StringComparison.Ordinal))
                timeProvider.Advance(TimeSpan.FromSeconds(31));
            else if (String.Equals(resource, "c", StringComparison.Ordinal))
                timeProvider.Advance(TimeSpan.FromSeconds(1));
            return Task.CompletedTask;
        });

        // Act
        await using var lockInstance = await locker.TryAcquireAsync(["a", "b", "c"], TimeSpan.FromMinutes(1), cancellationToken: TestCancellationToken);

        // Assert
        Assert.NotNull(lockInstance);
        Assert.Equal(TimeSpan.FromSeconds(59), await cache.GetExpirationAsync("lock:a"));
        Assert.True(await inner.IsLockedAsync("a"));
        Assert.True(await inner.IsLockedAsync("b"));
        Assert.True(await inner.IsLockedAsync("c"));
    }

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
        var locker = CreateMockLockProvider(timeProvider, ("a", first.Object));
        locker.Setup(p => p.TryAcquireAsync("b", It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(() =>
        {
            timeProvider.Advance(TimeSpan.FromSeconds(31));
            return duringRenewal ? Task.FromResult<ILock?>(second.Object) : Task.FromException<ILock?>(error);
        });

        // Act
        var thrown = await Assert.ThrowsAsync<CacheException>(() => locker.Object.TryAcquireAsync(["a", "b"], TimeSpan.FromMinutes(1), cancellationToken: TestCancellationToken));

        // Assert
        Assert.Same(error, thrown);
        first.Verify(l => l.ReleaseAsync(), Times.Once);
        second.Verify(l => l.ReleaseAsync(), duringRenewal ? Times.Once() : Times.Never());
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
        var locker = CreateMockLockProvider(timeProvider, ("a", failing.Object), ("b", lost.Object), ("c", last.Object));
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

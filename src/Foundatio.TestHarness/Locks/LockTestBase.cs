using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Lock;
using Foundatio.Utility;
using Foundatio.Xunit;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Locks;

public abstract class LockTestBase : TestWithLoggingBase
{
    protected LockTestBase(ITestOutputHelper output) : base(output)
    {
    }

    protected virtual ILockProvider GetThrottlingLockProvider(int maxHits, TimeSpan period)
    {
        return null;
    }

    protected virtual ILockProvider GetLockProvider()
    {
        return null;
    }

    public virtual async Task CanAcquireAndReleaseLockAsync()
    {
        Log.SetLogLevel<CacheLockProvider>(LogLevel.Trace);

        var locker = GetLockProvider();
        if (locker == null)
            return;

        string lockName = Guid.NewGuid().ToString("N")[..10];
        await using var lock1 = await locker.AcquireAsync(lockName, acquireTimeout: TimeSpan.FromMilliseconds(100), timeUntilExpires: TimeSpan.FromSeconds(1));

        try
        {
            Assert.NotNull(lock1);
            Assert.True(await locker.IsLockedAsync(lockName));
            var lock2Task = locker.AcquireAsync(lockName, acquireTimeout: TimeSpan.FromMilliseconds(250));
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            Assert.Null(await lock2Task);
        }
        finally
        {
            await lock1.ReleaseAsync();
        }

        Assert.False(await locker.IsLockedAsync(lockName));

        int counter = 0;

        await Parallel.ForEachAsync(Enumerable.Range(1, 25), async (_, _) =>
        {
            bool success = await locker.TryUsingAsync(lockName, () =>
            {
                Interlocked.Increment(ref counter);
            }, acquireTimeout: TimeSpan.FromSeconds(10));

            Assert.True(success);
        });

        Assert.Equal(25, counter);
    }

    public virtual async Task CanReleaseLockMultipleTimes()
    {
        var locker = GetLockProvider();
        if (locker == null)
            return;

        string lockName = Guid.NewGuid().ToString("N")[..10];
        var lock1 = await locker.AcquireAsync(lockName, acquireTimeout: TimeSpan.FromMilliseconds(100), timeUntilExpires: TimeSpan.FromSeconds(1));
        await lock1.ReleaseAsync();
        Assert.False(await locker.IsLockedAsync(lockName));

        await using var lock2 = await locker.AcquireAsync(lockName, acquireTimeout: TimeSpan.FromMilliseconds(100), timeUntilExpires: TimeSpan.FromSeconds(1));

        // has already been released, should not release other people's lock
        await lock1.ReleaseAsync();
        Assert.True(await locker.IsLockedAsync(lockName));

        // has already been released, should not release other people's lock
        await lock1.DisposeAsync();
        Assert.True(await locker.IsLockedAsync(lockName));

        await lock2.ReleaseAsync();
        Assert.False(await locker.IsLockedAsync(lockName));
    }

    public virtual async Task LockWillTimeoutAsync()
    {
        Log.SetLogLevel<InMemoryCacheClient>(LogLevel.Trace);
        Log.SetLogLevel<CacheLockProvider>(LogLevel.Trace);
        Log.SetLogLevel<ScheduledTimer>(LogLevel.Trace);

        var locker = GetLockProvider();
        if (locker == null)
            return;

        string lockName = Guid.NewGuid().ToString("N")[..10];

        _logger.LogInformation("Acquiring lock attempt #1");
        var testLock = await locker.AcquireAsync(lockName, timeUntilExpires: TimeSpan.FromMilliseconds(250));
        if (testLock is not null)
            _logger.LogInformation("Acquired lock attempt #1");
        else
            _logger.LogError("Unable to acquire lock attempt #1");
        Assert.NotNull(testLock);

        _logger.LogInformation("Acquiring lock attempt #2");
        testLock = await locker.AcquireAsync(lockName, acquireTimeout: TimeSpan.FromMilliseconds(50));
        if (testLock is not null)
            _logger.LogError("Acquired lock attempt #2");
        else
            _logger.LogInformation("Unable to acquire lock attempt #2");
        Assert.Null(testLock);

        _logger.LogInformation("Acquiring lock attempt #3");
        testLock = await locker.AcquireAsync(lockName, acquireTimeout: TimeSpan.FromSeconds(10));
        if (testLock is not null)
            _logger.LogInformation("Acquired lock attempt #3");
        else
            _logger.LogError("Unable to acquire lock attempt #3");
        Assert.NotNull(testLock);

        // Cleanup
        await testLock.DisposeAsync();
    }

    [Fact]
    public virtual async Task LockWontTimeoutEarly()
    {
        Log.SetLogLevel<InMemoryCacheClient>(LogLevel.Trace);
        Log.SetLogLevel<CacheLockProvider>(LogLevel.Trace);
        Log.SetLogLevel<ScheduledTimer>(LogLevel.Trace);

        var locker = GetLockProvider();
        if (locker == null)
            return;

        string lockName = Guid.NewGuid().ToString("N")[..10];

        _logger.LogInformation("Acquiring lock attempt #1");
        var testLock = await locker.AcquireAsync(lockName, timeUntilExpires: TimeSpan.FromSeconds(1));
        if (testLock is not null)
            _logger.LogInformation("Acquired lock attempt #1");
        else
            _logger.LogError("Unable to acquire lock attempt #1");
        Assert.NotNull(testLock);

        _logger.LogInformation("Acquiring lock attempt #2");
        var testLock2 = await locker.AcquireAsync(lockName, acquireTimeout: TimeSpan.FromMilliseconds(500));
        if (testLock2 is not null)
            _logger.LogError("Acquired lock attempt #2");
        else
            _logger.LogInformation("Unable to acquire lock attempt #2");
        Assert.Null(testLock2);

        _logger.LogInformation("Renew lock attempt #1");
        await testLock.RenewAsync(timeUntilExpires: TimeSpan.FromSeconds(1));

        _logger.LogInformation("Acquiring lock attempt #3");
        testLock = await locker.AcquireAsync(lockName, acquireTimeout: TimeSpan.FromMilliseconds(500));
        if (testLock is not null)
            _logger.LogError("Acquired lock attempt #3");
        else
            _logger.LogInformation("Unable to acquire lock attempt #3");
        Assert.Null(testLock);

        var sw = Stopwatch.StartNew();
        _logger.LogInformation("Acquiring lock attempt #4");
        testLock = await locker.AcquireAsync(lockName, acquireTimeout: TimeSpan.FromSeconds(5));
        sw.Stop();
        if (testLock is not null)
            _logger.LogInformation("Acquired lock attempt #4");
        else
            _logger.LogError("Unable to acquire lock attempt #4");
        Assert.NotNull(testLock);
        Assert.True(sw.ElapsedMilliseconds > 400);

        // Cleanup
        await testLock.DisposeAsync();
    }

    public virtual async Task CanAcquireMultipleResources()
    {
        Log.SetLogLevel<InMemoryCacheClient>(LogLevel.Trace);
        Log.SetLogLevel<CacheLockProvider>(LogLevel.Trace);
        Log.SetLogLevel<ScheduledTimer>(LogLevel.Trace);

        var locker = GetLockProvider();
        if (locker == null)
            return;

        var resources = new List<string> { "test1", "test2", "test3", "test4", "test5" };
        await using var testLock = await locker.AcquireAsync(resources, timeUntilExpires: TimeSpan.FromMilliseconds(250));
        if (testLock is not null)
            _logger.LogInformation("Acquired lock attempt #1");
        else
            _logger.LogError("Unable to acquire lock attempt #1");
        Assert.NotNull(testLock);

        resources.Add("other");
        await using var testLock2 = await locker.AcquireAsync(resources, timeUntilExpires: TimeSpan.FromMilliseconds(250), acquireTimeout: TimeSpan.FromMilliseconds(10));
        if (testLock2 is not null)
            _logger.LogError("Acquired lock attempt #2");
        else
            _logger.LogInformation("Unable to acquire lock attempt #2");
        Assert.Null(testLock2);

        await testLock.RenewAsync();
        await testLock.ReleaseAsync();

        await using var testLock3 = await locker.AcquireAsync(resources, timeUntilExpires: TimeSpan.FromMilliseconds(250), acquireTimeout: TimeSpan.FromMilliseconds(10));
        if (testLock3 is not null)
            _logger.LogInformation("Acquired lock attempt #3");
        else
            _logger.LogError("Unable to acquire lock attempt #3");
        Assert.NotNull(testLock3);
    }

    public virtual async Task CanAcquireMultipleScopedResources()
    {
        Log.SetLogLevel<InMemoryCacheClient>(LogLevel.Trace);
        Log.SetLogLevel<CacheLockProvider>(LogLevel.Trace);
        Log.SetLogLevel<ScheduledTimer>(LogLevel.Trace);

        var locker = GetLockProvider();
        if (locker == null)
            return;

        locker = new ScopedLockProvider(locker, "myscope");

        var resources = new List<string> { "test1", "test2", "test3", "test4", "test5" };
        await using var testLock = await locker.AcquireAsync(resources, timeUntilExpires: TimeSpan.FromMilliseconds(250));
        if (testLock is not null)
            _logger.LogInformation("Acquired lock attempt #1");
        else
            _logger.LogError("Unable to acquire lock attempt #1");
        Assert.NotNull(testLock);

        resources.Add("other");
        await using var testLock2 = await locker.AcquireAsync(resources, timeUntilExpires: TimeSpan.FromMilliseconds(250), acquireTimeout: TimeSpan.FromMilliseconds(10));
        if (testLock2 is not null)
            _logger.LogError("Acquired lock attempt #2");
        else
            _logger.LogInformation("Unable to acquire lock attempt #2");
        Assert.Null(testLock2);

        await testLock.RenewAsync();
        await testLock.ReleaseAsync();

        await using var testLock3 = await locker.AcquireAsync(resources, timeUntilExpires: TimeSpan.FromMilliseconds(250), acquireTimeout: TimeSpan.FromMilliseconds(10));
        if (testLock3 is not null)
            _logger.LogInformation("Acquired lock attempt #3");
        else
            _logger.LogError("Unable to acquire lock attempt #3");
        Assert.NotNull(testLock3);
    }

    public virtual async Task CanAcquireLocksInParallel()
    {
        var locker = GetLockProvider();
        if (locker == null)
            return;

        Log.SetLogLevel<CacheLockProvider>(LogLevel.Trace);

        const int COUNT = 100;
        int current = 1;
        var used = new List<int>();
        int concurrency = 0;
        string lockName = Guid.NewGuid().ToString("N")[..10];

        await Parallel.ForEachAsync(Enumerable.Range(1, COUNT), async (_, ct) =>
        {
            await using var myLock = await locker.AcquireAsync(lockName, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            Assert.NotNull(myLock);

            int currentConcurrency = Interlocked.Increment(ref concurrency);
            Assert.Equal(1, currentConcurrency);

            int item = current;
            await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.NextInt64(5, 25)), ct);
            used.Add(item);
            current++;

            Interlocked.Decrement(ref concurrency);
        });

        var duplicates = used.GroupBy(x => x).Where(g => g.Count() > 1);
        Assert.Empty(duplicates);
        Assert.Equal(COUNT, used.Count);
    }

    public virtual async Task CanAcquireScopedLocksInParallel()
    {
        var lockProvider = GetLockProvider();
        if (lockProvider == null)
            return;

        var locker = new ScopedLockProvider(lockProvider, "scoped");

        Log.SetLogLevel<CacheLockProvider>(LogLevel.Debug);

        const int COUNT = 100;
        int current = 1;
        var used = new List<int>();
        int concurrency = 0;
        string lockName = Guid.NewGuid().ToString("N")[..10];

        await Parallel.ForEachAsync(Enumerable.Range(1, COUNT), async (_, ct) =>
        {
            await using var myLock = await locker.AcquireAsync(lockName, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            Assert.NotNull(myLock);

            int currentConcurrency = Interlocked.Increment(ref concurrency);
            Assert.Equal(1, currentConcurrency);

            int item = current;
            await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.NextInt64(5, 25)), ct);
            used.Add(item);
            current++;

            Interlocked.Decrement(ref concurrency);
        });

        var duplicates = used.GroupBy(x => x).Where(g => g.Count() > 1);
        Assert.Empty(duplicates);
        Assert.Equal(COUNT, used.Count);
    }

    public virtual async Task CanAcquireMultipleLocksInParallel()
    {
        var locker = GetLockProvider();
        if (locker == null)
            return;

        Log.SetLogLevel<CacheLockProvider>(LogLevel.Debug);

        const int COUNT = 100;
        int current = 1;
        var used = new List<int>();
        int concurrency = 0;
        string lockName = Guid.NewGuid().ToString("N")[..10];

        await Parallel.ForEachAsync(Enumerable.Range(1, COUNT), async (_, ct) =>
        {
            await using var myLock = await locker.AcquireAsync([lockName, $"{lockName}2"], TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            Assert.NotNull(myLock);

            int currentConcurrency = Interlocked.Increment(ref concurrency);
            Assert.Equal(1, currentConcurrency);

            int item = current;
            await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.NextInt64(5, 25)), ct);
            used.Add(item);
            current++;

            Interlocked.Decrement(ref concurrency);
        });

        var duplicates = used.GroupBy(x => x).Where(g => g.Count() > 1);
        Assert.Empty(duplicates);
        Assert.Equal(COUNT, used.Count);
    }

    public virtual async Task LockOneAtATimeAsync()
    {
        var locker = GetLockProvider();
        if (locker == null)
            return;

        Log.SetLogLevel<CacheLockProvider>(LogLevel.Trace);

        int successCount = 0;
        var lockTask1 = Task.Run(async () =>
        {
            if (await DoLockedWorkAsync(locker))
            {
                Interlocked.Increment(ref successCount);
                _logger.LogInformation("LockTask1 Success");
            }
        });
        var lockTask2 = Task.Run(async () =>
        {
            if (await DoLockedWorkAsync(locker))
            {
                Interlocked.Increment(ref successCount);
                _logger.LogInformation("LockTask2 Success");
            }
        });
        var lockTask3 = Task.Run(async () =>
        {
            if (await DoLockedWorkAsync(locker))
            {
                Interlocked.Increment(ref successCount);
                _logger.LogInformation("LockTask3 Success");
            }
        });
        var lockTask4 = Task.Run(async () =>
        {
            if (await DoLockedWorkAsync(locker))
            {
                Interlocked.Increment(ref successCount);
                _logger.LogInformation("LockTask4 Success");
            }
        });

        await Task.WhenAll(lockTask1, lockTask2, lockTask3, lockTask4);
        Assert.Equal(1, successCount);

        await Task.Run(async () =>
        {
            if (await DoLockedWorkAsync(locker))
                Interlocked.Increment(ref successCount);
        });
        Assert.Equal(2, successCount);
    }

    private static Task<bool> DoLockedWorkAsync(ILockProvider locker)
    {
        return locker.TryUsingAsync("DoLockedWork", async () => await Task.Delay(500), TimeSpan.FromMinutes(1), TimeSpan.Zero);
    }

    public virtual async Task WillThrottleCallsAsync()
    {
        Log.SetLogLevel<ScheduledTimer>(LogLevel.Information);
        Log.SetLogLevel<ThrottlingLockProvider>(LogLevel.Trace);

        const int allowedLocks = 25;

        var period = TimeSpan.FromSeconds(2);
        var locker = GetThrottlingLockProvider(allowedLocks, period);
        if (locker == null)
            return;

        string lockName = Guid.NewGuid().ToString("N")[..10];

        // sleep until the start of the throttling period
        while (DateTime.UtcNow.Ticks % period.Ticks < TimeSpan.TicksPerMillisecond * 100)
            Thread.Sleep(10);

        var sw = Stopwatch.StartNew();
        for (int i = 1; i <= allowedLocks; i++)
        {
            _logger.LogInformation("Allowed Locks: {Id}", i);
            var l = await locker.AcquireAsync(lockName);
            Assert.NotNull(l);
        }
        sw.Stop();

        _logger.LogInformation("Time to acquire {AllowedLocks} locks: {Elapsed:g}", allowedLocks, sw.Elapsed);
        Assert.True(sw.Elapsed.TotalSeconds < 1);

        sw.Restart();
        var result = await locker.AcquireAsync(lockName, cancellationToken: new CancellationToken(true));
        sw.Stop();
        _logger.LogInformation("Total acquire time took to attempt to get throttled lock: {Elapsed:g}", sw.Elapsed);
        Assert.Null(result);

        sw.Restart();
        result = await locker.AcquireAsync(lockName, acquireTimeout: TimeSpan.FromSeconds(2.5));
        sw.Stop();
        _logger.LogInformation("Time to acquire lock: {Elapsed:g}", sw.Elapsed);
        Assert.NotNull(result);

        // Cleanup
        await result.DisposeAsync();
    }
}

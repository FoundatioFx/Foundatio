using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Lock;
using Foundatio.Logging.Xunit;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Locks {
    public abstract class LockTestBase : TestWithLoggingBase {
        protected LockTestBase(ITestOutputHelper output) : base(output) {
        }

        protected virtual ILockProvider GetThrottlingLockProvider(int maxHits, TimeSpan period) {
            return null;
        }

        protected virtual ILockProvider GetLockProvider() {
            return null;
        }

        public virtual async Task CanAcquireAndReleaseLockAsync() {
            var locker = GetLockProvider();
            if (locker == null)
                return;

            await locker.ReleaseAsync("test");

            var lock1 = await locker.AcquireAsync("test", acquireTimeout: TimeSpan.FromMilliseconds(100), lockTimeout: TimeSpan.FromSeconds(1));

            try {
                Assert.NotNull(lock1);
                Assert.True(await locker.IsLockedAsync("test"));
                var lock2Task = locker.AcquireAsync("test", acquireTimeout: TimeSpan.FromMilliseconds(250));
                await SystemClock.SleepAsync(TimeSpan.FromMilliseconds(250));
                Assert.Null(await lock2Task);
            } finally {
                await lock1.ReleaseAsync();
            }

            Assert.False(await locker.IsLockedAsync("test"));

            int counter = 0;

            await Run.InParallelAsync(25, async i => {
                var sw = Stopwatch.StartNew();
                var lock2 = await locker.AcquireAsync("test", acquireTimeout: TimeSpan.FromSeconds(1));
                sw.Stop();

                try {
                    if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Lock {Id}: start", i);
                    string message = lock2 != null ? "Acquired" : "Unable to acquire";
                    if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Lock {Id}: {Message} in {ms}ms", i, message, sw.ElapsedMilliseconds);

                    Assert.NotNull(lock2);
                    Assert.True(await locker.IsLockedAsync("test"), $"Lock {i}: was acquired but is not locked");
                    Interlocked.Increment(ref counter);
                    if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Lock {Id}: end", i);
                } finally {
                    if (lock2 != null)
                        await lock2.ReleaseAsync();
                }
            });

            Assert.Equal(25, counter);
        }

        public virtual async Task LockWillTimeoutAsync() {
            Log.SetLogLevel<InMemoryCacheClient>(LogLevel.Information);

            var locker = GetLockProvider();
            if (locker == null)
                return;

            _logger.LogInformation("Releasing lock");
            await locker.ReleaseAsync("test");

            _logger.LogInformation("Acquiring lock #1");
            var testLock = await locker.AcquireAsync("test", lockTimeout: TimeSpan.FromMilliseconds(250));
            _logger.LogInformation(testLock != null ? "Acquired lock #1" : "Unable to acquire lock #1");
            Assert.NotNull(testLock);

            _logger.LogInformation("Acquiring lock #2");
            testLock = await locker.AcquireAsync("test", acquireTimeout: TimeSpan.FromMilliseconds(50));
            _logger.LogInformation(testLock != null ? "Acquired lock #2" : "Unable to acquire lock #2");
            Assert.Null(testLock);

            _logger.LogInformation("Acquiring lock #3");
            testLock = await locker.AcquireAsync("test", acquireTimeout: TimeSpan.FromMilliseconds(300));
            _logger.LogInformation(testLock != null ? "Acquired lock #3" : "Unable to acquire lock #3");
            Assert.NotNull(testLock);
        }

        public virtual async Task LockOneAtATimeAsync() {
            var locker = GetLockProvider();
            if (locker == null)
                return;

            await locker.ReleaseAsync("DoLockedWork");
            Log.SetLogLevel<CacheLockProvider>(LogLevel.Trace);

            int successCount = 0;
            var lockTask1 = Task.Run(async () => {
                if (await DoLockedWorkAsync(locker)) {
                    Interlocked.Increment(ref successCount);
                    _logger.LogInformation("LockTask1 Success");
                }
            });
            var lockTask2 = Task.Run(async () => {
                if (await DoLockedWorkAsync(locker)) {
                    Interlocked.Increment(ref successCount);
                    _logger.LogInformation("LockTask2 Success");
                }
            });
            var lockTask3 = Task.Run(async () => {
                if (await DoLockedWorkAsync(locker)) {
                    Interlocked.Increment(ref successCount);
                    _logger.LogInformation("LockTask3 Success");
                }
            });
            var lockTask4 = Task.Run(async () => {
                if (await DoLockedWorkAsync(locker)) {
                    Interlocked.Increment(ref successCount);
                    _logger.LogInformation("LockTask4 Success");
                }
            });

            await Task.WhenAll(lockTask1, lockTask2, lockTask3, lockTask4);
            Assert.Equal(1, successCount);

            await Task.Run(async () => {
                if (await DoLockedWorkAsync(locker))
                    Interlocked.Increment(ref successCount);
            });
            Assert.Equal(2, successCount);
        }

        private async Task<bool> DoLockedWorkAsync(ILockProvider locker) {
            return await locker.TryUsingAsync("DoLockedWork", async () => await SystemClock.SleepAsync(500), TimeSpan.FromMinutes(1), TimeSpan.Zero);
        }

        public virtual async Task WillThrottleCallsAsync() {
            const int allowedLocks = 25;

            var period = TimeSpan.FromSeconds(2);
            var locker = GetThrottlingLockProvider(allowedLocks, period);
            if (locker == null)
                return;

            string lockName = Guid.NewGuid().ToString("N").Substring(10);

            // sleep until start of throttling period
            var utcNow = SystemClock.UtcNow;
            await SystemClock.SleepAsync(utcNow.Ceiling(period) - utcNow);
            var sw = Stopwatch.StartNew();
            for (int i = 1; i <= allowedLocks; i++) {
                if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("Allowed Locks: {Id}", i);
                var l = await locker.AcquireAsync(lockName);
                Assert.NotNull(l);
            }
            sw.Stop();

            if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("Time to acquire {AllowedLocks} locks: {Elapsed:g}", allowedLocks, sw.Elapsed);
            Assert.True(sw.Elapsed.TotalSeconds < 1);

            sw.Restart();
            var result = await locker.AcquireAsync(lockName, acquireTimeout: TimeSpan.FromMilliseconds(350));
            sw.Stop();
            if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("Total acquire time took to attempt to get throttled lock: {Elapsed:g}", sw.Elapsed);
            Assert.Null(result);

            sw.Restart();
            result = await locker.AcquireAsync(lockName, acquireTimeout: TimeSpan.FromSeconds(2.0));
            sw.Stop();
            if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("Time to acquire lock: {Elapsed:g}", sw.Elapsed);
            Assert.NotNull(result);
        }
    }
}

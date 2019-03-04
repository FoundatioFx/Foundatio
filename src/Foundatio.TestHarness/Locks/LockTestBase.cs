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
        protected LockTestBase(ITestOutputHelper output) : base(output) {}

        protected virtual ILockProvider GetThrottlingLockProvider(int maxHits, TimeSpan period) {
            return null;
        }

        protected virtual ILockProvider GetLockProvider() {
            return null;
        }

        public virtual async Task CanAcquireAndReleaseLockAsync() {
            Log.SetLogLevel<CacheLockProvider>(LogLevel.Trace);

            var locker = GetLockProvider();
            if (locker == null)
                return;

            var lock1 = await locker.AcquireAsync("test", acquireTimeout: TimeSpan.FromMilliseconds(100), timeUntilExpires: TimeSpan.FromSeconds(1));

            try {
                Assert.NotNull(lock1);
                Assert.True(await locker.IsLockedAsync("test"));
                var lock2Task = locker.AcquireAsync("test", acquireTimeout: TimeSpan.FromMilliseconds(250));
                await Time.DelayAsync(TimeSpan.FromMilliseconds(250));
                Assert.Null(await lock2Task);
            } finally {
                await lock1.ReleaseAsync();
            }

            Assert.False(await locker.IsLockedAsync("test"));

            int counter = 0;

            bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
            await Run.InParallelAsync(25, async i => {
                bool success = await locker.TryUsingAsync("test", () => {
                    Interlocked.Increment(ref counter);
                }, acquireTimeout: TimeSpan.FromSeconds(10));

                Assert.True(success);
            });

            Assert.Equal(25, counter);
        }

        public virtual async Task CanReleaseLockMultipleTimes() {
            var locker = GetLockProvider();
            if (locker == null)
                return;

            var lock1 = await locker.AcquireAsync("test", acquireTimeout: TimeSpan.FromMilliseconds(100), timeUntilExpires: TimeSpan.FromSeconds(1));
            await lock1.ReleaseAsync();
            Assert.False(await locker.IsLockedAsync("test"));

            var lock2 = await locker.AcquireAsync("test", acquireTimeout: TimeSpan.FromMilliseconds(100), timeUntilExpires: TimeSpan.FromSeconds(1));
            
            // has already been released, should not release other people's lock
            await lock1.ReleaseAsync();
            Assert.True(await locker.IsLockedAsync("test"));
            
            // has already been released, should not release other people's lock
            await lock1.DisposeAsync();
            Assert.True(await locker.IsLockedAsync("test"));

            await lock2.ReleaseAsync();
            Assert.False(await locker.IsLockedAsync("test"));
        }

        public virtual async Task LockWillTimeoutAsync() {
            Log.SetLogLevel<InMemoryCacheClient>(LogLevel.Trace);
            Log.SetLogLevel<CacheLockProvider>(LogLevel.Trace);
            Log.SetLogLevel<ScheduledTimer>(LogLevel.Trace);

            var locker = GetLockProvider();
            if (locker == null)
                return;

            _logger.LogInformation("Acquiring lock #1");
            var testLock = await locker.AcquireAsync("test", timeUntilExpires: TimeSpan.FromMilliseconds(250));
            _logger.LogInformation(testLock != null ? "Acquired lock #1" : "Unable to acquire lock #1");
            Assert.NotNull(testLock);

            _logger.LogInformation("Acquiring lock #2");
            testLock = await locker.AcquireAsync("test", acquireTimeout: TimeSpan.FromMilliseconds(50));
            _logger.LogInformation(testLock != null ? "Acquired lock #2" : "Unable to acquire lock #2");
            Assert.Null(testLock);

            _logger.LogInformation("Acquiring lock #3");
            testLock = await locker.AcquireAsync("test", acquireTimeout: TimeSpan.FromSeconds(10));
            _logger.LogInformation(testLock != null ? "Acquired lock #3" : "Unable to acquire lock #3");
            Assert.NotNull(testLock);
        }

        public virtual async Task LockOneAtATimeAsync() {
            var locker = GetLockProvider();
            if (locker == null)
                return;

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
            return await locker.TryUsingAsync("DoLockedWork", async () => await Time.DelayAsync(500), TimeSpan.FromMinutes(1), TimeSpan.Zero);
        }

        public virtual async Task WillThrottleCallsAsync() {
            Log.MinimumLevel = LogLevel.Trace;
            Log.SetLogLevel<ScheduledTimer>(LogLevel.Information);
            Log.SetLogLevel<ThrottlingLockProvider>(LogLevel.Trace);

            using (var time = Time.UseTestTime()) {
                const int allowedLocks = 25;

                var period = TimeSpan.FromSeconds(2);
                var locker = GetThrottlingLockProvider(allowedLocks, period);
                if (locker == null)
                    return;

                string lockName = Guid.NewGuid().ToString("N").Substring(10);

                var startTime = new DateTime(2019, 2, 27, 12, 0, 0);
                time.SetFrozenTime(startTime);

                var sw = Stopwatch.StartNew();
                for (int i = 1; i <= allowedLocks; i++) {
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
                
                time.AddTime(period);
                sw.Restart();
                result = await locker.AcquireAsync(lockName, acquireTimeout: TimeSpan.FromSeconds(1));
                sw.Stop();
                _logger.LogInformation("Time to acquire lock: {Elapsed:g}", sw.Elapsed);
                Assert.NotNull(result);
                
                time.AddTime(period);
                sw.Restart();
                result = await locker.AcquireAsync(lockName, cancellationToken: new CancellationToken(true));
                sw.Stop();
                _logger.LogInformation("Time to acquire lock: {Elapsed:g}", sw.Elapsed);
                Assert.NotNull(result);
            }
        }
    }
}

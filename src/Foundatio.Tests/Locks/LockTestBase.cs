using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Lock;
using Foundatio.Logging;
using Foundatio.Logging.Xunit;
using Foundatio.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Locks {
    public abstract class LockTestBase : TestWithLoggingBase {
        protected LockTestBase(ITestOutputHelper output) : base(output) { }

        protected virtual ILockProvider GetThrottlingLockProvider(int maxHits, TimeSpan period) {
            return null;
        }

        protected virtual ILockProvider GetLockProvider() {
            return null;
        }

        public virtual async Task CanAcquireAndReleaseLock() {
            var locker = GetLockProvider();
            if (locker == null)
                return;

            await locker.ReleaseAsync("test");

            var lock1 = await locker.AcquireAsync("test", acquireTimeout: TimeSpan.FromMilliseconds(100), lockTimeout: TimeSpan.FromSeconds(1));

            try {
                Assert.NotNull(lock1);
                Assert.True(await locker.IsLockedAsync("test"));
                var lock2 = await locker.AcquireAsync("test", acquireTimeout: TimeSpan.FromMilliseconds(250));
                Assert.Null(lock2);
            } finally {
                await lock1.ReleaseAsync();
            }

            Assert.False(await locker.IsLockedAsync("test"));

            int counter = 0;

            await Run.InParallel(25, async i => {
                var sw = Stopwatch.StartNew();
                var lock2 = await locker.AcquireAsync("test", acquireTimeout: TimeSpan.FromSeconds(1));
                sw.Stop();

                try {
                    _logger.Trace("Lock {i}: start", i);
                    string message = lock2 != null ? "Acquired" : "Unable to acquire";
                    _logger.Trace("Lock {i}: {message} in {ms}ms", i, message, sw.ElapsedMilliseconds);

                    Assert.NotNull(lock2);
                    Assert.True(await locker.IsLockedAsync("test"), $"Lock {i}: was acquired but is not locked");
                    Interlocked.Increment(ref counter);
                    _logger.Trace("Lock {i}: end", i);
                } finally {
                    if (lock2 != null)
                        await lock2.ReleaseAsync().AnyContext();
                }
            });

            Assert.Equal(25, counter);
        }

        public virtual async Task LockWillTimeout() {
            var locker = GetLockProvider();
            if (locker == null)
                return;

            _logger.Info("Releasing lock");
            await locker.ReleaseAsync("test");
                
            _logger.Info("Acquiring lock #1");
            var testLock = await locker.AcquireAsync("test", lockTimeout: TimeSpan.FromMilliseconds(150));
            _logger.Info(testLock != null ? "Acquired lock" : "Unable to acquire lock");
            Assert.NotNull(testLock);

            _logger.Info("Acquiring lock #2");
            testLock = await locker.AcquireAsync("test", acquireTimeout: TimeSpan.FromMilliseconds(100));
            _logger.Info(testLock != null ? "Acquired lock" : "Unable to acquire lock");
            Assert.Null(testLock);

            _logger.Info("Acquiring lock #3");
            testLock = await locker.AcquireAsync("test", acquireTimeout: TimeSpan.FromMilliseconds(100));
            _logger.Info(testLock != null ? "Acquired lock" : "Unable to acquire lock");
            Assert.NotNull(testLock);
        }

        public virtual async Task LockOneAtATime() {
            Log.SetLogLevel<CacheLockProvider>(LogLevel.Trace);

            var locker = GetLockProvider();
            if (locker == null)
                return;

            int successCount = 0;
            var configTask1 = Task.Run(() => {
                if (DoLockedWork(locker).GetAwaiter().GetResult())
                    Interlocked.Increment(ref successCount);
            });
            var configTask2 = Task.Run(() => {
                if (DoLockedWork(locker).GetAwaiter().GetResult())
                    Interlocked.Increment(ref successCount);
            });
            var configTask3 = Task.Run(() => {
                if (DoLockedWork(locker).GetAwaiter().GetResult())
                    Interlocked.Increment(ref successCount);
            });
            var configTask4 = Task.Run(() => {
                if (DoLockedWork(locker).GetAwaiter().GetResult())
                    Interlocked.Increment(ref successCount);
            });

            await Task.WhenAll(configTask1, configTask2, configTask3, configTask4);
            Assert.Equal(1, successCount);

            await Task.Run(() => {
                if (DoLockedWork(locker).GetAwaiter().GetResult())
                    Interlocked.Increment(ref successCount);
            });
            Assert.Equal(2, successCount);
        }

        private async Task<bool> DoLockedWork(ILockProvider locker) {
            return await locker.TryUsingAsync("DoLockedWork", () => Thread.Sleep(500), TimeSpan.FromMinutes(1), TimeSpan.Zero).AnyContext();
        }

        public virtual async Task WillThrottleCalls() {
            Log.MinimumLevel = LogLevel.Trace;

            const int allowedLocks = 25;
            var period = TimeSpan.FromSeconds(1);
            var locker = GetThrottlingLockProvider(allowedLocks, period);
            if (locker == null)
                return;

            var lockName = Guid.NewGuid().ToString("N").Substring(10);
            await locker.ReleaseAsync(lockName);

            // sleep until start of throttling period
            await Task.Delay(DateTime.Now.Ceiling(period) - DateTime.Now);

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < allowedLocks; i++) {
                _logger.Info($"Allowed Locks: {i}");
                var l = await locker.AcquireAsync(lockName);
                Assert.NotNull(l);
            }
            sw.Stop();

            _logger.Info(sw.Elapsed.ToString());
            Assert.True(sw.Elapsed.TotalSeconds < 1);
            
            sw.Restart();
            var result = await locker.AcquireAsync(lockName, acquireTimeout: TimeSpan.FromMilliseconds(250));
            sw.Stop();
            Assert.Null(result);
            _logger.Info(sw.Elapsed.ToString());
            
            sw.Restart();
            result = await locker.AcquireAsync(lockName, acquireTimeout: TimeSpan.FromSeconds(1.5));
            sw.Stop();
            Assert.NotNull(result);
            _logger.Info(sw.Elapsed.ToString());
        }
    }
}

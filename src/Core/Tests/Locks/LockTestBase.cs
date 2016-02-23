using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Lock;
using Foundatio.Logging;
using Foundatio.Tests.Utility;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Locks {
    public abstract class LockTestBase : TestBase {
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

            using (locker) {
                await locker.ReleaseAsync("test");

                using (var lock1 = await locker.AcquireAsync("test", acquireTimeout: TimeSpan.FromMilliseconds(100), lockTimeout: TimeSpan.FromSeconds(1))) {
                    Assert.NotNull(lock1);
                    Assert.True(await locker.IsLockedAsync("test"));
                    var lock2 = await locker.AcquireAsync("test", acquireTimeout: TimeSpan.FromMilliseconds(250));
                    Assert.Null(lock2);
                }

                Assert.False(await locker.IsLockedAsync("test"));

                int counter = 0;

                await Run.InParallel(25, async i => {
                    var sw = Stopwatch.StartNew();
                    using (var lock1 = await locker.AcquireAsync("test", acquireTimeout: TimeSpan.FromSeconds(1))) {
                        sw.Stop();
                        _logger.Trace().Message($"Lock {i}: start").Write();
                        string message = lock1 != null ? "Acquired" : "Unable to acquire";
                        _logger.Trace().Message($"Lock {i}: {message} in {sw.ElapsedMilliseconds}ms").Write();

                        Assert.NotNull(lock1);
                        Assert.True(await locker.IsLockedAsync("test"), $"Lock {i}: was acquired but is not locked");
                        Interlocked.Increment(ref counter);
                        _logger.Trace().Message($"Lock {i}: end").Write();
                    }
                });

                Assert.Equal(25, counter);
            }
        }

        public virtual async Task LockWillTimeout() {
            var locker = GetLockProvider();
            if (locker == null)
                return;

            using (locker) {
                _logger.Info().Message("Releasing lock").Write();
                await locker.ReleaseAsync("test");
                
                _logger.Info().Message("Acquiring lock #1").Write();
                var testLock = await locker.AcquireAsync("test", lockTimeout: TimeSpan.FromMilliseconds(150));
                _logger.Info().Message(testLock != null ? "Acquired lock" : "Unable to acquire lock").Write();
                Assert.NotNull(testLock);

                _logger.Info().Message("Acquiring lock #2").Write();
                testLock = await locker.AcquireAsync("test", acquireTimeout: TimeSpan.FromMilliseconds(100));
                _logger.Info().Message(testLock != null ? "Acquired lock" : "Unable to acquire lock").Write();
                Assert.Null(testLock);

                _logger.Info().Message("Acquiring lock #3").Write();
                testLock = await locker.AcquireAsync("test", acquireTimeout: TimeSpan.FromMilliseconds(100));
                _logger.Info().Message(testLock != null ? "Acquired lock" : "Unable to acquire lock").Write();
                Assert.NotNull(testLock);
            }
        }

        public virtual async Task LockOneAtATime() {
            var locker = GetLockProvider();
            if (locker == null)
                return;

            using (locker) {
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
        }

        private async Task<bool> DoLockedWork(ILockProvider locker) {
            using (
                var l = await locker.AcquireAsync("DoLockedWork", TimeSpan.FromMinutes(1), TimeSpan.Zero).AnyContext()) {
                if (l == null)
                    return false;

                Thread.Sleep(200);

                return true;
            }
        }

        public virtual async Task WillThrottleCalls() {
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
                var l = await locker.AcquireAsync(lockName);
                Assert.NotNull(l);
            }
            sw.Stop();

            _logger.LogInformation(sw.Elapsed.ToString());
            Assert.True(sw.Elapsed.TotalSeconds < 1);
            
            sw.Restart();
            var result = await locker.AcquireAsync(lockName, acquireTimeout: TimeSpan.FromMilliseconds(250));
            sw.Stop();
            Assert.Null(result);
            _logger.LogInformation(sw.Elapsed.ToString());
            
            sw.Restart();
            result = await locker.AcquireAsync(lockName, acquireTimeout: TimeSpan.FromSeconds(1.5));
            sw.Stop();
            Assert.NotNull(result);
            _logger.LogInformation(sw.Elapsed.ToString());
        }
    }
}

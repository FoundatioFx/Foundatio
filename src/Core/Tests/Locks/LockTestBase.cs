using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Lock;
using Foundatio.Logging;
using Foundatio.Tests.Utility;
using Foundatio.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Locks {
    public abstract class LockTestBase : CaptureTests {
        protected LockTestBase(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output) { }

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
                await locker.ReleaseLockAsync("test");

                using (var lock1 = await locker.AcquireLockAsync("test", acquireTimeout: TimeSpan.FromMilliseconds(100), lockTimeout: TimeSpan.FromSeconds(1))) {
                    Assert.NotNull(lock1);
                    Assert.True(await locker.IsLockedAsync("test"));
                    var lock2 = await locker.AcquireLockAsync("test", acquireTimeout: TimeSpan.FromMilliseconds(250));
                    Assert.Null(lock2);
                }

                Assert.False(await locker.IsLockedAsync("test"));

                int counter = 0;

                await Run.InParallel(25, async i => {
                    var sw = Stopwatch.StartNew();
                    using (var lock1 = await locker.AcquireLockAsync("test", acquireTimeout: TimeSpan.FromSeconds(1))) {
                        sw.Stop();
                        Logger.Trace().Message($"Lock {i}: start").Write();
                        string message = lock1 != null ? "Acquired" : "Unable to acquire";
                        Logger.Trace().Message($"Lock {i}: {message} in {sw.ElapsedMilliseconds}ms").Write();

                        Assert.NotNull(lock1);
                        Assert.True(await locker.IsLockedAsync("test"), $"Lock {i}: was acquired but is not locked");
                        Interlocked.Increment(ref counter);
                        Logger.Trace().Message($"Lock {i}: end").Write();
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
                Logger.Info().Message("Releasing lock").Write();
                await locker.ReleaseLockAsync("test");
                
                Logger.Info().Message("Acquiring lock #1").Write();
                var testLock = await locker.AcquireLockAsync("test", lockTimeout: TimeSpan.FromMilliseconds(150));
                Logger.Info().Message(testLock != null ? "Acquired lock" : "Unable to acquire lock").Write();
                Assert.NotNull(testLock);

                Logger.Info().Message("Acquiring lock #2").Write();
                testLock = await locker.AcquireLockAsync("test", acquireTimeout: TimeSpan.FromMilliseconds(100));
                Logger.Info().Message(testLock != null ? "Acquired lock" : "Unable to acquire lock").Write();
                Assert.Null(testLock);

                Logger.Info().Message("Acquiring lock #3").Write();
                testLock = await locker.AcquireLockAsync("test", acquireTimeout: TimeSpan.FromMilliseconds(100));
                Logger.Info().Message(testLock != null ? "Acquired lock" : "Unable to acquire lock").Write();
                Assert.NotNull(testLock);
            }
        }
        
        public virtual async Task WillThrottleCalls() {
            var period = TimeSpan.FromSeconds(1);
            var locker = GetThrottlingLockProvider(5, period);
            if (locker == null)
                return;

            var lockName = Guid.NewGuid().ToString("N").Substring(10);
            await locker.ReleaseLockAsync(lockName);

            // sleep until start of throttling period
            await Task.Delay(DateTime.Now.Ceiling(period) - DateTime.Now);

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 5; i++) {
                var l = await locker.AcquireLockAsync(lockName);
                Assert.NotNull(l);
            }
            sw.Stop();

            _output.WriteLine(sw.Elapsed.ToString());
            Assert.True(sw.Elapsed.TotalSeconds < 1);
            
            sw.Restart();
            var result = await locker.AcquireLockAsync(lockName, acquireTimeout: TimeSpan.FromMilliseconds(250));
            sw.Stop();
            Assert.Null(result);
            _output.WriteLine(sw.Elapsed.ToString());
            
            sw.Restart();
            result = await locker.AcquireLockAsync(lockName, acquireTimeout: TimeSpan.FromSeconds(1.5));
            sw.Stop();
            Assert.NotNull(result);
            _output.WriteLine(sw.Elapsed.ToString());
        }
    }
}

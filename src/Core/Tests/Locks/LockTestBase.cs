using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Lock;
using Foundatio.Logging;
using Foundatio.Tests.Utility;
using Foundatio.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests {
    public abstract class LockTestBase : CaptureTests {
        protected virtual ILockProvider GetLockProvider() {
            return null;
        }

        public virtual async Task CanAcquireAndReleaseLock() {
            var locker = GetLockProvider();
            if (locker == null)
                return;

            using (locker) {
                await locker.ReleaseLockAsync("test").AnyContext();

                using (var lock1 = await locker.AcquireLockAsync("test", acquireTimeout: TimeSpan.FromMilliseconds(100), lockTimeout: TimeSpan.FromSeconds(1)).AnyContext()) {
                    Assert.NotNull(lock1);
                    Assert.True(await locker.IsLockedAsync("test").AnyContext());
                    var lock2 = await locker.AcquireLockAsync("test", acquireTimeout: TimeSpan.FromMilliseconds(250)).AnyContext();
                    Assert.Null(lock2);
                }

                Assert.False(await locker.IsLockedAsync("test").AnyContext());

                int counter = 0;

                await Run.InParallel(25, async i => {
                    using (var lock1 = await locker.AcquireLockAsync("test", acquireTimeout: TimeSpan.FromSeconds(1)).AnyContext()) {
                        Assert.NotNull(lock1);
                        Assert.True(await locker.IsLockedAsync("test").AnyContext());
                        Interlocked.Increment(ref counter);
                    }
                }).AnyContext();

                Assert.Equal(25, counter);
            }
        }

        public virtual async Task LockWillTimeout() {
            var locker = GetLockProvider();
            if (locker == null)
                return;

            using (locker) {
                Logger.Info().Message("Releasing lock").Write();
                await locker.ReleaseLockAsync("test").AnyContext();
                
                Logger.Info().Message("Acquiring lock #1").Write();
                var testLock = await locker.AcquireLockAsync("test", lockTimeout: TimeSpan.FromMilliseconds(150)).AnyContext();
                Logger.Info().Message(testLock != null ? "Acquired lock" : "Unable to acquire lock").Write();
                Assert.NotNull(testLock);

                Logger.Info().Message("Acquiring lock #2").Write();
                testLock = await locker.AcquireLockAsync("test", acquireTimeout: TimeSpan.FromMilliseconds(100)).AnyContext();
                Logger.Info().Message(testLock != null ? "Acquired lock" : "Unable to acquire lock").Write();
                Assert.Null(testLock);

                Logger.Info().Message("Acquiring lock #3").Write();
                testLock = await locker.AcquireLockAsync("test", acquireTimeout: TimeSpan.FromMilliseconds(100)).AnyContext();
                Logger.Info().Message(testLock != null ? "Acquired lock" : "Unable to acquire lock").Write();
                Assert.NotNull(testLock);
            }
        }

        protected LockTestBase(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output) { }
    }
}

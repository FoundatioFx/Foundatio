using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Lock;
using Xunit;

namespace Foundatio.Tests {
    public abstract class LockTestBase {
        protected virtual ILockProvider GetLockProvider() {
            return null;
        }

        public virtual void CanAcquireAndReleaseLock() {
            var locker = GetLockProvider();
            if (locker == null)
                return;

            locker.ReleaseLock("test");

            using (locker.AcquireLock("test", acquireTimeout: TimeSpan.FromSeconds(1))) {
                Assert.True(locker.IsLocked("test"));
                Assert.Throws<TimeoutException>(() => locker.AcquireLock("test", acquireTimeout: TimeSpan.FromMilliseconds(100)));
            }

            Assert.False(locker.IsLocked("test"));

            int counter = 0;
            Parallel.For(0, 20, i => {
                using (locker.AcquireLock("test")) {
                    Assert.True(locker.IsLocked("test"));
                    Interlocked.Increment(ref counter);
                }
            });

            Assert.Equal(20, counter);
        }

        public virtual void LockWillTimeout() {
            var locker = GetLockProvider();
            if (locker == null)
                return;

            locker.ReleaseLock("test");

            var testLock = locker.AcquireLock("test", TimeSpan.FromSeconds(1));
            Assert.NotNull(testLock);

            Assert.Throws<TimeoutException>(() => locker.AcquireLock("test", acquireTimeout: TimeSpan.FromMilliseconds(100)));

            testLock = locker.AcquireLock("test", acquireTimeout: TimeSpan.FromSeconds(2));
            Assert.NotNull(testLock);
        }
    }
}

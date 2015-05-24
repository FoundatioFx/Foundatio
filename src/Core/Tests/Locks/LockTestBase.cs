using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Lock;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests {
    public class TestBase {
        protected TextWriter _writer;

        public TestBase(ITestOutputHelper helper) {
            _writer = new TestOutputWriter(helper);
        }
    }

    public abstract class LockTestBase : CaptureTests {
        protected virtual ILockProvider GetLockProvider() {
            return null;
        }

        public virtual void CanAcquireAndReleaseLock() {
            var locker = GetLockProvider();
            if (locker == null)
                return;

            using (locker) {
                locker.ReleaseLock("test");

                using (locker.AcquireLock("test", acquireTimeout: TimeSpan.FromSeconds(1))) {
                    Assert.True(locker.IsLocked("test"));
                    Assert.Throws<TimeoutException>(() => locker.AcquireLock("test", acquireTimeout: TimeSpan.FromMilliseconds(250)));
                }

                Assert.False(locker.IsLocked("test"));

                int counter = 0;
                Parallel.For(0, 25, i => {
                    using (locker.AcquireLock("test", acquireTimeout: TimeSpan.FromSeconds(1))) {
                        Assert.True(locker.IsLocked("test"));
                        Interlocked.Increment(ref counter);
                    }
                });

                Assert.Equal(25, counter);
            }
        }

        public virtual void LockWillTimeout() {
            var locker = GetLockProvider();
            if (locker == null)
                return;

            using (locker) {
                locker.ReleaseLock("test");

                var testLock = locker.AcquireLock("test", TimeSpan.FromSeconds(1));
                Assert.NotNull(testLock);

                Assert.Throws<TimeoutException>(() => locker.AcquireLock("test", acquireTimeout: TimeSpan.FromMilliseconds(100)));

                testLock = locker.AcquireLock("test", acquireTimeout: TimeSpan.FromSeconds(2));
                Assert.NotNull(testLock);
            }
        }

        protected LockTestBase(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output) { }
    }
}

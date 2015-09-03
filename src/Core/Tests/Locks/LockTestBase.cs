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

                using (var lock1 = locker.AcquireLock("test", acquireTimeout: TimeSpan.FromSeconds(1))) {
                    Assert.NotNull(lock1);
                    Assert.True(locker.IsLocked("test"));
                    var lock2 = locker.AcquireLock("test", acquireTimeout: TimeSpan.FromMilliseconds(250));
                    Assert.Null(lock2);
                }

                Assert.False(locker.IsLocked("test"));

                int counter = 0;
                Parallel.For(0, 25, i => {
                    using (var lock1 = locker.AcquireLock("test", acquireTimeout: TimeSpan.FromSeconds(1))) {
                        Assert.NotNull(lock1);
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

                var testLock = locker.AcquireLock("test", TimeSpan.FromMilliseconds(100));
                Assert.NotNull(testLock);
                var lock1 = locker.AcquireLock("test", acquireTimeout: TimeSpan.FromMilliseconds(100));
                Assert.Null(lock1);

                testLock = locker.AcquireLock("test", acquireTimeout: TimeSpan.FromMilliseconds(100));
                Assert.NotNull(testLock);
            }
        }

        protected LockTestBase(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output) { }
    }
}

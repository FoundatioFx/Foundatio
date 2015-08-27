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

        public virtual async Task CanAcquireAndReleaseLock() {
            var locker = GetLockProvider();
            if (locker == null)
                return;

            using (locker) {
                await locker.ReleaseLockAsync("test");

                using (var lock1 = await locker.AcquireLockAsync("test", acquireTimeout: TimeSpan.FromSeconds(1))) {
                    Assert.NotNull(lock1);
                    Assert.True(await locker.IsLockedAsync("test"));
                    var lock2 = await locker.AcquireLockAsync("test", acquireTimeout: TimeSpan.FromMilliseconds(250));
                    Assert.Null(lock2);
                }

                Assert.False(await locker.IsLockedAsync("test"));

                int counter = 0;
                Parallel.For(0, 25, async i => {
                    using (var lock1 = await locker.AcquireLockAsync("test", acquireTimeout: TimeSpan.FromSeconds(1))) {
                        Assert.NotNull(lock1);
                        Assert.True(await locker.IsLockedAsync("test"));
                        Interlocked.Increment(ref counter);
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
                await locker.ReleaseLockAsync("test");

                var testLock = await locker.AcquireLockAsync("test", TimeSpan.FromSeconds(1));
                Assert.NotNull(testLock);
                var lock1 = await locker.AcquireLockAsync("test", acquireTimeout: TimeSpan.FromMilliseconds(100));
                Assert.Null(lock1);

                testLock = await locker.AcquireLockAsync("test", acquireTimeout: TimeSpan.FromSeconds(2));
                Assert.NotNull(testLock);
            }
        }

        protected LockTestBase(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output) { }
    }
}

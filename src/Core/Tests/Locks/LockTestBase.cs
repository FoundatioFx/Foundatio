using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
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
                await locker.ReleaseLockAsync("test").AnyContext();

                using (var lock1 = await locker.AcquireLockAsync("test", acquireTimeout: TimeSpan.FromSeconds(1)).AnyContext()) {
                    Assert.NotNull(lock1);
                    Assert.True(await locker.IsLockedAsync("test").AnyContext());
                    var lock2 = await locker.AcquireLockAsync("test", acquireTimeout: TimeSpan.FromMilliseconds(250)).AnyContext();
                    Assert.Null(lock2);
                }

                Assert.False(await locker.IsLockedAsync("test").AnyContext());

                int counter = 0;
                Parallel.For(0, 25, i => {
                    using (var lock1 = locker.AcquireLockAsync("test", acquireTimeout: TimeSpan.FromSeconds(1)).Result) {
                        Assert.NotNull(lock1);
                        Assert.True(locker.IsLockedAsync("test").Result);
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
                await locker.ReleaseLockAsync("test").AnyContext();

                var testLock = await locker.AcquireLockAsync("test", TimeSpan.FromSeconds(1)).AnyContext();
                Assert.NotNull(testLock);
                var lock1 = await locker.AcquireLockAsync("test", acquireTimeout: TimeSpan.FromMilliseconds(100)).AnyContext();
                Assert.Null(lock1);

                testLock = await locker.AcquireLockAsync("test", acquireTimeout: TimeSpan.FromSeconds(2)).AnyContext();
                Assert.NotNull(testLock);
            }
        }

        protected LockTestBase(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output) { }
    }
}

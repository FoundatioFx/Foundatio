using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Extensions;
using Foundatio.Lock;
using Foundatio.Messaging;
using Foundatio.Tests.Utility;
using Nito.AsyncEx;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Locks {
    public class InMemoryLockTests : LockTestBase {
        public InMemoryLockTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output) {}

        protected override ILockProvider GetLockProvider() {
            return new CacheLockProvider(new InMemoryCacheClient(), new InMemoryMessageBus());
        }

        [Fact]
        public override Task CanAcquireAndReleaseLock() {
            return base.CanAcquireAndReleaseLock();
        }

        [Fact]
        public override Task LockWillTimeout() {
            return base.LockWillTimeout();
        }

        [Fact]
        public async Task WillPassthrowResetEvent() {
            var resetEvent = new AsyncManualResetEvent(false);
            var sw = Stopwatch.StartNew();
            await Task.WhenAny(Task.Delay(100), resetEvent.WaitAsync()).AnyContext();
            sw.Stop();
            Assert.InRange(sw.ElapsedMilliseconds, 100, 110);

            sw.Restart();
            resetEvent.Reset();
            sw.Stop();
            Assert.InRange(sw.ElapsedMilliseconds, 0, 5);

            sw.Reset();
            await Task.WhenAny(Task.Delay(100), resetEvent.WaitAsync()).AnyContext();
            sw.Stop();
            Assert.InRange(sw.ElapsedMilliseconds, 100, 110);
            
            sw.Reset();
            await Task.WhenAny(Task.Delay(100), Task.Factory.StartNewDelayed(10, () => resetEvent.Set()), resetEvent.WaitAsync()).AnyContext();
            sw.Stop();
            Assert.InRange(sw.ElapsedMilliseconds, 10, 50);

            sw.Restart();
            resetEvent.Reset();
            sw.Stop();
            Assert.InRange(sw.ElapsedMilliseconds, 0, 5);
        }
    }
}
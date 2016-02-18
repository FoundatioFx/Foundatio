using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Lock;
using Foundatio.Messaging;
using Foundatio.Tests.Utility;
using Nito.AsyncEx;
using Xunit;
using Xunit.Abstractions;
using Foundatio.Extensions;

namespace Foundatio.Tests.Locks {
    public class InMemoryLockTests : LockTestBase {
        public InMemoryLockTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output) {}

        protected override ILockProvider GetThrottlingLockProvider(int maxHits, TimeSpan period) {
            return new ThrottlingLockProvider(new InMemoryCacheClient(), maxHits, period);
        }

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
        public override Task LockOneAtATime() {
            return base.LockOneAtATime();
        }

        [Fact]
        public async Task WillPulseMonitor() {
            var monitor = new AsyncMonitor();
            var sw = Stopwatch.StartNew();
            // Monitor will not be pulsed and should be cancelled after 100ms.
            using (await monitor.EnterAsync())
                await Assert.ThrowsAsync<TaskCanceledException>(async () =>
                    await monitor.WaitAsync(TimeSpan.FromMilliseconds(100).ToCancellationToken()));
            sw.Stop();
            Assert.InRange(sw.ElapsedMilliseconds, 75, 125);

            var t = Task.Run(async () => {
                await Task.Delay(25);
                using (await monitor.EnterAsync())
                    monitor.Pulse();
            });

            sw = Stopwatch.StartNew();
            using (await monitor.EnterAsync())
                await monitor.WaitAsync(TimeSpan.FromSeconds(1).ToCancellationToken());
            sw.Stop();
            Assert.InRange(sw.ElapsedMilliseconds, 25, 100);
        }

        [Fact]
        public override Task WillThrottleCalls() {
            return base.WillThrottleCalls();
        }
    }
}
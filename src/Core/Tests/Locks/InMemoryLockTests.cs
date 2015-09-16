using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Extensions;
using Foundatio.Lock;
using Foundatio.Logging;
using Foundatio.Messaging;
using Foundatio.Tests.Utility;
using Nito.AsyncEx;
using Xunit;
using Xunit.Abstractions;

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
        public override Task WillThrottleCalls() {
            return base.WillThrottleCalls();
        }

        [Fact]
        public async Task WillWaitForMonitor() {
            var monitor = new AsyncMonitor();

            // 1. Fall through with the delay.
            var sw = Stopwatch.StartNew();
            var cancellationTokenSource = new CancellationTokenSource(100);
            try {
                using (await monitor.EnterAsync(cancellationTokenSource.Token))
                    await monitor.WaitAsync(cancellationTokenSource.Token).AnyContext();
            } catch (TaskCanceledException) { }
            sw.Stop();
            Assert.InRange(sw.ElapsedMilliseconds, 100, 125);
            Assert.True(cancellationTokenSource.IsCancellationRequested);
            
            // 2. Wait for the pulse to be set.
            sw.Restart();
            using (await monitor.EnterAsync()) {
                Task.Run(async () => {
                    await Task.Delay(50).AnyContext();
                    Logger.Trace().Message("Pulse").Write();
                    monitor.Pulse();
                    Logger.Trace().Message("Pulsed").Write();
                });

                Logger.Trace().Message("Waiting").Write();
                await monitor.WaitAsync().AnyContext();
            }

            sw.Stop();
            Assert.InRange(sw.ElapsedMilliseconds, 50, 100);
        }
    }
}
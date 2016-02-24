using System;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Metrics;
using Foundatio.Tests.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Metrics {
    public class InMemoryMetricsTests : TestWithLoggingBase {
        public InMemoryMetricsTests(ITestOutputHelper output) : base(output) {}

        [Fact]
        public async Task CanIncrementCounter() {
            var metrics = new InMemoryMetricsClient(false, loggerFactory: Log);

            await metrics.CounterAsync("c1");
            Assert.Equal(1, await metrics.GetCounterCountAsync("c1"));

            await metrics.CounterAsync("c1", 5);
            Assert.Equal(6, await metrics.GetCounterCountAsync("c1"));

            await metrics.GaugeAsync("g1", 2.534);
            Assert.Equal(2.534, await metrics.GetLastGaugeValueAsync("g1"));

            await metrics.TimerAsync("t1", 50788);
            var timer = await metrics.GetTimerStatsAsync("t1");
            Assert.Equal(1, timer.Count);

            _logger.Info((await metrics.GetCounterStatsAsync("c1")).ToString());
        }

        [Fact]
        public async Task CanSendBufferedMetrics() {
            var metrics = new InMemoryMetricsClient(loggerFactory: Log);

            Parallel.For(0, 100, i => metrics.CounterAsync("c1").GetAwaiter().GetResult());

            await metrics.FlushAsync();

            var counter = await metrics.GetCounterStatsAsync("c1");
            Assert.Equal(100, counter.Count);
        }

#pragma warning disable 4014
        [Fact]
        public async Task CanWaitForCounter() {
            var metrics = new InMemoryMetricsClient(false, loggerFactory: Log);
            Task.Run(async () => {
                await Task.Delay(50);
                await metrics.CounterAsync("Test").AnyContext();
                await metrics.CounterAsync("Test").AnyContext();
            });

            var success = await metrics.WaitForCounterAsync("Test", 1, TimeSpan.FromMilliseconds(500));
            Assert.True(success);

            Task.Run(async () => {
                await Task.Delay(50);
                await metrics.CounterAsync("Test").AnyContext();
            });

            success = await metrics.WaitForCounterAsync("Test", timeout: TimeSpan.FromMilliseconds(500));
            Assert.True(success);

            success = await metrics.WaitForCounterAsync("Test", timeout: TimeSpan.FromMilliseconds(100));
            Assert.False(success);

            Task.Run(async () => {
                await Task.Delay(50);
                await metrics.CounterAsync("Test", 2);
            });

            success = await metrics.WaitForCounterAsync("Test", 2, TimeSpan.FromMilliseconds(500));
            Assert.True(success);

            success = await metrics.WaitForCounterAsync("Test", async () => await metrics.CounterAsync("Test"), cancellationToken: TimeSpan.FromMilliseconds(500).ToCancellationToken());
            Assert.True(success);

            Task.Run(async () => {
                await Task.Delay(50);
                await metrics.CounterAsync("Test");
            });

            success = await metrics.WaitForCounterAsync("Test", timeout: TimeSpan.FromMilliseconds(500));
            Assert.True(success);

            _logger.Info((await metrics.GetCounterStatsAsync("Test")).ToString());
        }
#pragma warning restore 4014
    }
}
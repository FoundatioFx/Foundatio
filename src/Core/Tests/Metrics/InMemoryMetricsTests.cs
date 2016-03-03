using System;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Logging.Xunit;
using Foundatio.Metrics;
using Foundatio.Queues;
using Foundatio.Tests.Queue;
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

        [Fact]
        public async Task CanGetQueueMetrics() {
            var metrics = new InMemoryMetricsClient(loggerFactory: Log);
            var queue = new InMemoryQueue<SimpleWorkItem>(behaviors: new[] { new MetricsQueueBehavior<SimpleWorkItem>(metrics, loggerFactory: Log) }, loggerFactory: Log);

            await queue.EnqueueAsync(new SimpleWorkItem { Id = 1, Data = "1" });
            await Task.Delay(50);
            var entry = await queue.DequeueAsync(TimeSpan.Zero);
            await Task.Delay(30);
            await entry.CompleteAsync();
            await Task.Delay(500); // give queue metrics time

            await metrics.FlushAsync();

            var queueStats = await metrics.GetQueueStatsAsync("simpleworkitem");
            Assert.Equal(1, queueStats.Count.Max);
            Assert.Equal(0, queueStats.Count.Last);
            Assert.Equal(1, queueStats.Enqueued.Count);
            Assert.InRange(queueStats.QueueTime.AverageDuration, 50, 100);
            Assert.Equal(1, queueStats.Dequeued.Count);
            Assert.Equal(1, queueStats.Completed.Count);
            Assert.InRange(queueStats.ProcessTime.AverageDuration, 30, 100);
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

            await Task.Delay(1);
            var success = await metrics.WaitForCounterAsync("Test", 1, TimeSpan.FromMilliseconds(500));
            Assert.True(success);

            Task.Run(async () => {
                await Task.Delay(50);
                await metrics.CounterAsync("Test").AnyContext();
            });

            await Task.Delay(1);
            success = await metrics.WaitForCounterAsync("Test", timeout: TimeSpan.FromMilliseconds(500));
            Assert.True(success);

            await Task.Delay(1);
            success = await metrics.WaitForCounterAsync("Test", timeout: TimeSpan.FromMilliseconds(100));
            Assert.False(success);

            Task.Run(async () => {
                await Task.Delay(50);
                await metrics.CounterAsync("Test", 2);
            });

            await Task.Delay(1);
            success = await metrics.WaitForCounterAsync("Test", 2, TimeSpan.FromMilliseconds(500));
            Assert.True(success);

            await Task.Delay(1);
            success = await metrics.WaitForCounterAsync("Test", async () => await metrics.CounterAsync("Test"), cancellationToken: TimeSpan.FromMilliseconds(500).ToCancellationToken());
            Assert.True(success);

            Task.Run(async () => {
                await Task.Delay(50);
                await metrics.CounterAsync("Test");
            });

            await Task.Delay(1);
            success = await metrics.WaitForCounterAsync("Test", timeout: TimeSpan.FromMilliseconds(500));
            Assert.True(success);

            _logger.Info((await metrics.GetCounterStatsAsync("Test")).ToString());
        }
#pragma warning restore 4014
    }
}
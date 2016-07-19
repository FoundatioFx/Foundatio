using System;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Logging.Xunit;
using Foundatio.Metrics;
using Foundatio.Queues;
using Foundatio.Tests.Queue;
using Foundatio.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Metrics {
    public abstract class MetricsClientTestBase : TestWithLoggingBase {
        public MetricsClientTestBase(ITestOutputHelper output) : base(output) {
            SystemClock.Reset();
        }

        public abstract IMetricsClient GetMetricsClient(bool buffered = false);

        public virtual async Task CanSetGaugesAsync() {
            using (var metrics = GetMetricsClient()) {
                var stats = metrics as IMetricsClientStats;
                if (stats == null)
                    return;

                await metrics.GaugeAsync("mygauge", 12d);
                Assert.Equal(12d, (await stats.GetGaugeStatsAsync("mygauge")).Last);
                await metrics.GaugeAsync("mygauge", 10d);
                await metrics.GaugeAsync("mygauge", 5d);
                await metrics.GaugeAsync("mygauge", 4d);
                await metrics.GaugeAsync("mygauge", 12d);
                await metrics.GaugeAsync("mygauge", 20d);
                Assert.Equal(20d, (await stats.GetGaugeStatsAsync("mygauge")).Last);
            }
        }

        public virtual async Task CanIncrementCounter() {
            using (var metrics = GetMetricsClient()) {
                var stats = metrics as IMetricsClientStats;
                if (stats == null)
                    return;

                await metrics.CounterAsync("c1");
                Assert.Equal(1, await stats.GetCounterCountAsync("c1"));

                await metrics.CounterAsync("c1", 5);
                Assert.Equal(6, await stats.GetCounterCountAsync("c1"));

                await metrics.GaugeAsync("g1", 2.534);
                Assert.Equal(2.534, await stats.GetLastGaugeValueAsync("g1"));

                await metrics.TimerAsync("t1", 50788);
                var timer = await stats.GetTimerStatsAsync("t1");
                Assert.Equal(1, timer.Count);

                _logger.Info((await stats.GetCounterStatsAsync("c1")).ToString());
            }
        }
        
        public virtual async Task CanGetBufferedQueueMetrics() {
            using (var metrics = GetMetricsClient(true) as IBufferedMetricsClient) {
                var stats = metrics as IMetricsClientStats;
                if (stats == null)
                    return;

                using (var queue = new InMemoryQueue<SimpleWorkItem>(behaviors: new[] { new MetricsQueueBehavior<SimpleWorkItem>(metrics, loggerFactory: Log) }, loggerFactory: Log)) {
                    await queue.EnqueueAsync(new SimpleWorkItem { Id = 1, Data = "1" });
                    await SystemClock.SleepAsync(50);
                    var entry = await queue.DequeueAsync(TimeSpan.Zero);
                    await SystemClock.SleepAsync(30);
                    await entry.CompleteAsync();
                    await SystemClock.SleepAsync(500);  // give queue metrics time

                    await metrics.FlushAsync();

                    var queueStats = await stats.GetQueueStatsAsync("simpleworkitem");
                    Assert.Equal(1, queueStats.Count.Max);
                    Assert.Equal(0, queueStats.Count.Last);
                    Assert.Equal(1, queueStats.Enqueued.Count);
                    Assert.Equal(1, queueStats.Dequeued.Count);
                    Assert.Equal(1, queueStats.Completed.Count);
                    Assert.InRange(queueStats.QueueTime.AverageDuration, 50, 200);
                    Assert.InRange(queueStats.ProcessTime.AverageDuration, 30, 200);
                }
            }
        }

        public virtual async Task CanIncrementBufferedCounter() {
            using (var metrics = GetMetricsClient(true) as IBufferedMetricsClient) {
                var stats = metrics as IMetricsClientStats;
                if (stats == null)
                    return;

                await metrics.CounterAsync("c1");
                await metrics.FlushAsync();
                var counter = await stats.GetCounterStatsAsync("c1", SystemClock.UtcNow.AddMinutes(-5), SystemClock.UtcNow);
                Assert.Equal(1, counter.Count);

                await metrics.CounterAsync("c1", 5);
                await metrics.FlushAsync();
                counter = await stats.GetCounterStatsAsync("c1", SystemClock.UtcNow.AddMinutes(-5), SystemClock.UtcNow);
                Assert.Equal(6, counter.Count);

                await metrics.GaugeAsync("g1", 5.34);
                await metrics.FlushAsync();
                var gauge = await stats.GetGaugeStatsAsync("g1", SystemClock.UtcNow.AddMinutes(-5), SystemClock.UtcNow);
                Assert.Equal(5.34, gauge.Last);
                Assert.Equal(5.34, gauge.Max);

                await metrics.GaugeAsync("g1", 2.534);
                await metrics.FlushAsync();
                gauge = await stats.GetGaugeStatsAsync("g1", SystemClock.UtcNow.AddMinutes(-5), SystemClock.UtcNow);
                Assert.Equal(2.534, gauge.Last);
                Assert.Equal(5.34, gauge.Max);

                await metrics.TimerAsync("t1", 50788);
                await metrics.FlushAsync();
                var timer = await stats.GetTimerStatsAsync("t1", SystemClock.UtcNow.AddMinutes(-5), SystemClock.UtcNow);
                Assert.Equal(1, timer.Count);
                Assert.Equal(50788, timer.TotalDuration);

                await metrics.TimerAsync("t1", 98);
                await metrics.TimerAsync("t1", 102);
                await metrics.FlushAsync();
                timer = await stats.GetTimerStatsAsync("t1", SystemClock.UtcNow.AddMinutes(-5), SystemClock.UtcNow);
                Assert.Equal(3, timer.Count);
                Assert.Equal(50788 + 98 + 102, timer.TotalDuration);
            }
        }

#pragma warning disable 4014
        public virtual async Task CanWaitForCounter() {
            using (var metrics = GetMetricsClient() as CacheBucketMetricsClientBase) {
                var stats = metrics as IMetricsClientStats;
                if (stats == null)
                    return;

                Task.Run(async () => {
                    await SystemClock.SleepAsync(50);
                    await metrics.CounterAsync("Test").AnyContext();
                    await metrics.CounterAsync("Test").AnyContext();
                });

                await SystemClock.SleepAsync(1);
                var success = await metrics.WaitForCounterAsync("Test", 1, TimeSpan.FromMilliseconds(500));
                Assert.True(success);

                Task.Run(async () => {
                    await SystemClock.SleepAsync(50);
                    await metrics.CounterAsync("Test").AnyContext();
                });

                await SystemClock.SleepAsync(1);
                success = await metrics.WaitForCounterAsync("Test", timeout: TimeSpan.FromMilliseconds(500));
                Assert.True(success);

                await SystemClock.SleepAsync(1);
                success = await metrics.WaitForCounterAsync("Test", timeout: TimeSpan.FromMilliseconds(100));
                Assert.False(success);

                Task.Run(async () => {
                    await SystemClock.SleepAsync(50);
                    await metrics.CounterAsync("Test", 2);
                });

                await SystemClock.SleepAsync(1);
                success = await metrics.WaitForCounterAsync("Test", 2, TimeSpan.FromMilliseconds(500));
                Assert.True(success);

                await SystemClock.SleepAsync(1);
                success = await metrics.WaitForCounterAsync("Test", async () => await metrics.CounterAsync("Test"), cancellationToken: TimeSpan.FromMilliseconds(500).ToCancellationToken());
                Assert.True(success);

                Task.Run(async () => {
                    await SystemClock.SleepAsync(50);
                    await metrics.CounterAsync("Test");
                });

                await SystemClock.SleepAsync(1);
                success = await metrics.WaitForCounterAsync("Test", timeout: TimeSpan.FromMilliseconds(500));
                Assert.True(success);

                _logger.Info((await metrics.GetCounterStatsAsync("Test")).ToString());
            }
        }
#pragma warning restore 4014

        public virtual async Task CanSendBufferedMetrics() {
            using (var metrics = GetMetricsClient(true) as IBufferedMetricsClient) {
                var stats = metrics as IMetricsClientStats;
                if (stats == null)
                    return;

                Parallel.For(0, 100, i => metrics.CounterAsync("c1").GetAwaiter().GetResult());

                await metrics.FlushAsync();

                var counter = await stats.GetCounterStatsAsync("c1");
                Assert.Equal(100, counter.Count);
            }
        }
    }
}
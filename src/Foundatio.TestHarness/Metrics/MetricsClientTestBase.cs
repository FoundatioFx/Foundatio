using System;
using System.Threading.Tasks;
using Foundatio.Logging.Xunit;
using Foundatio.Metrics;
using Foundatio.Queues;
using Foundatio.Tests.Queue;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
#pragma warning disable AsyncFixer04 // A disposable object used in a fire & forget async call

namespace Foundatio.Tests.Metrics {
    public abstract class MetricsClientTestBase : TestWithLoggingBase {
        public MetricsClientTestBase(ITestOutputHelper output) : base(output) {}

        public abstract IMetricsClient GetMetricsClient(bool buffered = false);

        public virtual async Task CanSetGaugesAsync() {
            using (var metrics = GetMetricsClient()) {
                if (!(metrics is IMetricsClientStats stats))
                    return;

                metrics.Gauge("mygauge", 12d);
                Assert.Equal(12d, (await stats.GetGaugeStatsAsync("mygauge")).Last);
                metrics.Gauge("mygauge", 10d);
                metrics.Gauge("mygauge", 5d);
                metrics.Gauge("mygauge", 4d);
                metrics.Gauge("mygauge", 12d);
                metrics.Gauge("mygauge", 20d);
                Assert.Equal(20d, (await stats.GetGaugeStatsAsync("mygauge")).Last);
            }
        }

        public virtual async Task CanIncrementCounterAsync() {
            using (var metrics = GetMetricsClient()) {
                if (!(metrics is IMetricsClientStats stats))
                    return;

                metrics.Counter("c1");
                await AssertCounterAsync(stats, "c1", 1);

                metrics.Counter("c1", 5);
                await AssertCounterAsync(stats, "c1", 6);

                metrics.Gauge("g1", 2.534);
                Assert.Equal(2.534, await stats.GetLastGaugeValueAsync("g1"));

                metrics.Timer("t1", 50788);
                var timer = await stats.GetTimerStatsAsync("t1");
                Assert.Equal(1, timer.Count);

                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation((await stats.GetCounterStatsAsync("c1")).ToString());
            }
        }

        private async Task AssertCounterAsync(IMetricsClientStats client, string name, long expected) {
            await Run.WithRetriesAsync(async () => {
                long actual = await client.GetCounterCountAsync(name, SystemClock.UtcNow.Subtract(TimeSpan.FromHours(1)));
                Assert.Equal(expected, actual);
            }, 8, logger: _logger);
        }

        public virtual async Task CanGetBufferedQueueMetricsAsync() {
            using (var metrics = GetMetricsClient(true) as IBufferedMetricsClient) {
                if (!(metrics is IMetricsClientStats stats))
                    return;

                using (var behavior = new MetricsQueueBehavior<SimpleWorkItem>(metrics, reportCountsInterval: TimeSpan.FromMilliseconds(25), loggerFactory: Log)) {
                    using (var queue = new InMemoryQueue<SimpleWorkItem>(new InMemoryQueueOptions<SimpleWorkItem> { Behaviors = new [] { behavior }, LoggerFactory = Log })) {
                        await queue.EnqueueAsync(new SimpleWorkItem { Id = 1, Data = "1" });
                        await SystemClock.SleepAsync(50);
                        var entry = await queue.DequeueAsync(TimeSpan.Zero);
                        await SystemClock.SleepAsync(15);
                        await entry.CompleteAsync();

                        await SystemClock.SleepAsync(100); // give queue metrics time
                        await metrics.FlushAsync();
                        var queueStats = await stats.GetQueueStatsAsync("simpleworkitem");
                        Assert.Equal(1, queueStats.Count.Max);
                        Assert.Equal(0, queueStats.Count.Last);
                        Assert.Equal(1, queueStats.Enqueued.Count);
                        Assert.Equal(1, queueStats.Dequeued.Count);
                        Assert.Equal(1, queueStats.Completed.Count);
                        Assert.InRange(queueStats.QueueTime.AverageDuration, 45, 250);
                        Assert.InRange(queueStats.ProcessTime.AverageDuration, 10, 250);
                    }
                }
            }
        }

        public virtual async Task CanIncrementBufferedCounterAsync() {
            using (var metrics = GetMetricsClient(true) as IBufferedMetricsClient) {
                if (!(metrics is IMetricsClientStats stats))
                    return;

                metrics.Counter("c1");
                await metrics.FlushAsync();
                var counter = await stats.GetCounterStatsAsync("c1", SystemClock.UtcNow.AddMinutes(-5), SystemClock.UtcNow);
                Assert.Equal(1, counter.Count);

                metrics.Counter("c1", 5);
                await metrics.FlushAsync();
                counter = await stats.GetCounterStatsAsync("c1", SystemClock.UtcNow.AddMinutes(-5), SystemClock.UtcNow);
                Assert.Equal(6, counter.Count);

                metrics.Gauge("g1", 5.34);
                await metrics.FlushAsync();
                var gauge = await stats.GetGaugeStatsAsync("g1", SystemClock.UtcNow.AddMinutes(-5), SystemClock.UtcNow);
                Assert.Equal(5.34, gauge.Last);
                Assert.Equal(5.34, gauge.Max);

                metrics.Gauge("g1", 2.534);
                await metrics.FlushAsync();
                gauge = await stats.GetGaugeStatsAsync("g1", SystemClock.UtcNow.AddMinutes(-5), SystemClock.UtcNow);
                Assert.Equal(2.534, gauge.Last);
                Assert.Equal(5.34, gauge.Max);

                metrics.Timer("t1", 50788);
                await metrics.FlushAsync();
                var timer = await stats.GetTimerStatsAsync("t1", SystemClock.UtcNow.AddMinutes(-5), SystemClock.UtcNow);
                Assert.Equal(1, timer.Count);
                Assert.Equal(50788, timer.TotalDuration);

                metrics.Timer("t1", 98);
                metrics.Timer("t1", 102);
                await metrics.FlushAsync();
                timer = await stats.GetTimerStatsAsync("t1", SystemClock.UtcNow.AddMinutes(-5), SystemClock.UtcNow);
                Assert.Equal(3, timer.Count);
                Assert.Equal(50788 + 98 + 102, timer.TotalDuration);
            }
        }

#pragma warning disable 4014
        public virtual async Task CanWaitForCounterAsync() {
            const string CounterName = "Test";
            using (var metrics = GetMetricsClient() as CacheBucketMetricsClientBase) {
                if (!(metrics is IMetricsClientStats stats))
                    return;

                Task.Run(async () => {
                    await SystemClock.SleepAsync(TimeSpan.FromMilliseconds(50));
                    metrics.Counter(CounterName);
                });

                var task = metrics.WaitForCounterAsync(CounterName, 1, TimeSpan.FromMilliseconds(500));
                await SystemClock.SleepAsync(TimeSpan.FromMilliseconds(50));
                Assert.True(await task, "Expected at least 1 count within 500 ms");

                Task.Run(async () => {
                    await SystemClock.SleepAsync(TimeSpan.FromMilliseconds(50));
                    metrics.Counter(CounterName);
                });

                task = metrics.WaitForCounterAsync(CounterName, timeout: TimeSpan.FromMilliseconds(500));
                await SystemClock.SleepAsync(TimeSpan.FromMilliseconds(50));
                Assert.True(await task, "Expected at least 2 count within 500 ms");

                Task.Run(async () => {
                    await SystemClock.SleepAsync(TimeSpan.FromMilliseconds(50));
                    metrics.Counter(CounterName, 2);
                });

                task = metrics.WaitForCounterAsync(CounterName, 2, TimeSpan.FromMilliseconds(500));
                await SystemClock.SleepAsync(TimeSpan.FromMilliseconds(50));
                Assert.True(await task, "Expected at least 4 count within 500 ms");

                task = metrics.WaitForCounterAsync(CounterName, () => {
                    metrics.Counter(CounterName);
                    return Task.CompletedTask;
                }, cancellationToken: TimeSpan.FromMilliseconds(500).ToCancellationToken());
                await SystemClock.SleepAsync(TimeSpan.FromMilliseconds(500));
                Assert.True(await task, "Expected at least 5 count within 500 ms");

                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation((await metrics.GetCounterStatsAsync(CounterName)).ToString());
            }
        }
#pragma warning restore 4014

        public virtual async Task CanSendBufferedMetricsAsync() {
            using (var metrics = GetMetricsClient(true) as IBufferedMetricsClient) {
                if (!(metrics is IMetricsClientStats stats))
                    return;

                Parallel.For(0, 100, i => metrics.Counter("c1"));

                await metrics.FlushAsync();

                var counter = await stats.GetCounterStatsAsync("c1");
                Assert.Equal(100, counter.Count);
            }
        }
    }
}
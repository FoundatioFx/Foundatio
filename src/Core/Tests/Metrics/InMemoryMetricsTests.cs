using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Logging;
using Foundatio.Metrics;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Metrics {
    public class InMemoryMetricsTests : CaptureTests {
        private readonly TestOutputWriter _writer;

        public InMemoryMetricsTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output)
        {
            MinimumLogLevel = LogLevel.Warn;
            _writer = new TestOutputWriter(output);
        }

        [Fact]
        public void CanIncrementCounter() {
            var metrics = new InMemoryMetricsClient();

            metrics.Counter("c1");
            Assert.Equal(1, metrics.GetCount("c1"));

            metrics.Counter("c1", 5);
            Assert.Equal(6, metrics.GetCount("c1"));

            var counter = metrics.Counters["c1"];
            Assert.True(counter.Rate > 400);

            metrics.Gauge("g1", 2.534);
            Assert.Equal(2.534, metrics.GetGaugeValue("g1"));

            metrics.Timer("t1", 50788);
            var stats = metrics.GetMetricStats();
            Assert.Equal(1, stats.Timings.Count);

            metrics.DisplayStats(_writer);
        }

        [Fact]
        public async void CanWaitForCounter() {
            var metrics = new InMemoryMetricsClient();
            metrics.StartDisplayingStats(TimeSpan.FromMilliseconds(50), _writer);
            Task.Run(() => {
                Thread.Sleep(50);
                metrics.Counter("Test");
                metrics.Counter("Test");
            });
            var success = await metrics.WaitForCounterAsync("Test", TimeSpan.FromMilliseconds(500), 2);
            Assert.True(success);

            Task.Run(() => {
                Thread.Sleep(50);
                metrics.Counter("Test");
            });
            success = await metrics.WaitForCounterAsync("Test", TimeSpan.FromMilliseconds(500));
            Assert.True(success);

            success = await metrics.WaitForCounterAsync("Test", TimeSpan.FromMilliseconds(100));
            Assert.False(success);

            Task.Run(() => {
                Thread.Sleep(50);
                metrics.Counter("Test", 2);
            });
            success = await metrics.WaitForCounterAsync("Test", TimeSpan.FromMilliseconds(500), 2);
            Assert.True(success);

            success = await metrics.WaitForCounterAsync("Test", TimeSpan.FromMilliseconds(500), 1,
                async () => await metrics.CounterAsync("Test"));
            Assert.True(success);

            Task.Run(() => {
                Thread.Sleep(50);
                metrics.Counter("Test");
            });
            success = metrics.WaitForCounter("Test", TimeSpan.FromMilliseconds(500));
            Assert.True(success);

            metrics.DisplayStats(_writer);
        }

        [Fact]
        public void CanDisplayStatsMultithreaded() {
            var metrics = new InMemoryMetricsClient();
            metrics.StartDisplayingStats(TimeSpan.FromMilliseconds(10), _writer);
            Parallel.For(0, 100, i => {
                metrics.Counter("Test");
                Thread.Sleep(50);
            });
        }
    }
}
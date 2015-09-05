using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Metrics;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Metrics {
    public class InMemoryMetricsTests : CaptureTests {
        private readonly TestOutputWriter _writer;

        public InMemoryMetricsTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output) {
            _writer = new TestOutputWriter(output);
        }

        [Fact]
        public async Task CanIncrementCounter() {
            var metrics = new InMemoryMetricsClient();

            await metrics.CounterAsync("c1").AnyContext();
            Assert.Equal(1, metrics.GetCount("c1"));

            await metrics.CounterAsync("c1", 5).AnyContext();
            Assert.Equal(6, metrics.GetCount("c1"));

            var counter = metrics.Counters["c1"];
            Assert.True(counter.Rate > 400);

            await metrics.GaugeAsync("g1", 2.534).AnyContext();
            Assert.Equal(2.534, metrics.GetGaugeValue("g1"));

            await metrics.TimerAsync("t1", 50788).AnyContext();
            var stats = metrics.GetMetricStats();
            Assert.Equal(1, stats.Timings.Count);

            metrics.DisplayStats(_writer);
        }

        [Fact]
        public async Task CanWaitForCounter() {
            var metrics = new InMemoryMetricsClient();
            metrics.StartDisplayingStats(TimeSpan.FromMilliseconds(50), _writer);
            Task.Run(async () => {
                Thread.Sleep(50);
                await metrics.CounterAsync("Test").AnyContext();
                await metrics.CounterAsync("Test").AnyContext();
            }).AnyContext();

            var success = await metrics.WaitForCounterAsync("Test", TimeSpan.FromMilliseconds(500), 2).AnyContext();
            Assert.True(success);

            Task.Run(async () => {
                Thread.Sleep(50);
                await metrics.CounterAsync("Test").AnyContext();
            }).AnyContext();

            success = await metrics.WaitForCounterAsync("Test", TimeSpan.FromMilliseconds(500)).AnyContext();
            Assert.True(success);

            success = await metrics.WaitForCounterAsync("Test", TimeSpan.FromMilliseconds(100)).AnyContext();
            Assert.False(success);

            Task.Run(async () => {
                Thread.Sleep(50);
                await metrics.CounterAsync("Test", 2).AnyContext();
            }).AnyContext();

            success = await metrics.WaitForCounterAsync("Test", TimeSpan.FromMilliseconds(500), 2).AnyContext();
            Assert.True(success);

            success = await metrics.WaitForCounterAsync("Test", async () => await metrics.CounterAsync("Test").AnyContext(), TimeSpan.FromMilliseconds(500)).AnyContext();
            Assert.True(success);

            Task.Run(async () => {
                Thread.Sleep(50);
                await metrics.CounterAsync("Test").AnyContext();
            }).AnyContext();

            success = await metrics.WaitForCounterAsync("Test", TimeSpan.FromMilliseconds(500)).AnyContext();
            Assert.True(success);

            metrics.DisplayStats(_writer);
        }

        [Fact]
        public Task CanDisplayStatsMultithreaded() {
            var metrics = new InMemoryMetricsClient();
            metrics.StartDisplayingStats(TimeSpan.FromMilliseconds(10), _writer);
            Parallel.For(0, 100, i => {
                metrics.CounterAsync("Test").AnyContext().GetAwaiter().GetResult();
                Thread.Sleep(50);
            });

            return Task.FromResult(0);
        }
    }
}
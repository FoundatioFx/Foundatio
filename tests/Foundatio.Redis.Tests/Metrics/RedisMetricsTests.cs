using System;
using System.Threading.Tasks;
using Foundatio.Metrics;
using Foundatio.Redis.Tests.Extensions;
using Foundatio.Tests.Metrics;
using xRetry;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Redis.Tests.Metrics {
    public class RedisMetricsTests : MetricsClientTestBase, IDisposable {
        public RedisMetricsTests(ITestOutputHelper output) : base(output) {
            var muxer = SharedConnection.GetMuxer();
            muxer.FlushAllAsync().GetAwaiter().GetResult();
        }

        public override IMetricsClient GetMetricsClient(bool buffered = false) {
            return new RedisMetricsClient(o => o.ConnectionMultiplexer(SharedConnection.GetMuxer()).Buffered(buffered).LoggerFactory(Log));
        }

        [Fact]
        public override Task CanSetGaugesAsync() {
            return base.CanSetGaugesAsync();
        }

        [Fact]
        public override Task CanIncrementCounterAsync() {
            return base.CanIncrementCounterAsync();
        }

        [RetryFact]
        public override Task CanWaitForCounterAsync() {
            return base.CanWaitForCounterAsync();
        }

        [Fact]
        public override Task CanGetBufferedQueueMetricsAsync() {
            return base.CanGetBufferedQueueMetricsAsync();
        }

        [Fact]
        public override Task CanIncrementBufferedCounterAsync() {
            return base.CanIncrementBufferedCounterAsync();
        }

        [Fact]
        public override Task CanSendBufferedMetricsAsync() {
            return base.CanSendBufferedMetricsAsync();
        }
        
        [Fact]
        public async Task SendGaugesAsync() {
            using (var metrics = GetMetricsClient()) {
                if (!(metrics is IMetricsClientStats stats))
                    return;

                int max = 1000;
                for (int index = 0; index <= max; index++) {
                    metrics.Gauge("mygauge", index);
                    metrics.Timer("mygauge", index);
                }

                Assert.Equal(max, (await stats.GetGaugeStatsAsync("mygauge")).Last);
            }
        }

        [Fact]
        public async Task SendGaugesBufferedAsync() {
            using (var metrics = GetMetricsClient(true)) {
                if (!(metrics is IMetricsClientStats stats))
                    return;

                int max = 1000;
                for (int index = 0; index <= max; index++) {
                    metrics.Gauge("mygauge", index);
                    metrics.Timer("mygauge", index);
                }

                if (metrics is IBufferedMetricsClient bufferedMetrics)
                    await bufferedMetrics.FlushAsync();

                Assert.Equal(max, (await stats.GetGaugeStatsAsync("mygauge")).Last);
            }
        }

        [Fact]
        public async Task SendRedisAsync() {
            var db = SharedConnection.GetMuxer().GetDatabase();

            int max = 1000;
            for (int index = 0; index <= max; index++) {
                await db.SetAddAsync("test", index);
            }
        }

        public void Dispose() {
            var muxer = SharedConnection.GetMuxer();
            muxer.FlushAllAsync().GetAwaiter().GetResult();
        }
    }
}
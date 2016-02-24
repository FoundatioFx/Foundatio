using System;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Logging;
using Foundatio.Redis.Metrics;
using Foundatio.Tests.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Redis.Tests.Metrics {
    public class RedisMetricsTests : TestWithLoggingBase {
        public RedisMetricsTests(ITestOutputHelper output) : base(output) { }

        [Fact]
        public async Task CanIncrementCounter() {
            var metrics = new RedisMetricsClient(SharedConnection.GetMuxer());
            FlushAll();

            await metrics.CounterAsync("c1");
            await metrics.FlushAsync();
            var counter = await metrics.GetCounterStatsAsync("c1", DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow);
            Assert.Equal(1, counter.Count);

            await metrics.CounterAsync("c1", 5);
            await metrics.FlushAsync();
            counter = await metrics.GetCounterStatsAsync("c1", DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow);
            Assert.Equal(6, counter.Count);

            await metrics.GaugeAsync("g1", 5.34);
            await metrics.FlushAsync();
            var gauge = await metrics.GetGaugeStatsAsync("g1", DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow);
            Assert.Equal(5.34, gauge.Last);
            Assert.Equal(5.34, gauge.Max);

            await metrics.GaugeAsync("g1", 2.534);
            await metrics.FlushAsync();
            gauge = await metrics.GetGaugeStatsAsync("g1", DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow);
            Assert.Equal(2.534, gauge.Last);
            Assert.Equal(5.34, gauge.Max);

            await metrics.TimerAsync("t1", 50788);
            await metrics.FlushAsync();
            var timer = await metrics.GetTimerStatsAsync("t1", DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow);
            Assert.Equal(1, timer.Count);
            Assert.Equal(50788, timer.TotalDuration);

            await metrics.TimerAsync("t1", 98);
            await metrics.TimerAsync("t1", 102);
            await metrics.FlushAsync();
            timer = await metrics.GetTimerStatsAsync("t1", DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow);
            Assert.Equal(3, timer.Count);
            Assert.Equal(50788 + 98 + 102, timer.TotalDuration);
        }

        private void FlushAll() {
            var endpoints = SharedConnection.GetMuxer().GetEndPoints(true);
            if (endpoints.Length == 0)
                return;

            foreach (var endpoint in endpoints) {
                var server = SharedConnection.GetMuxer().GetServer(endpoint);

                try {
                    server.FlushAllDatabases();
                } catch (Exception) { }
            }
        }

        private int CountAllKeys() {
            var endpoints = SharedConnection.GetMuxer().GetEndPoints(true);
            if (endpoints.Length == 0)
                return 0;

            int count = 0;
            foreach (var endpoint in endpoints) {
                var server = SharedConnection.GetMuxer().GetServer(endpoint);

                try {
                    var keys = server.Keys().ToArray();
                    foreach (var key in keys)
                        _logger.Info(key);
                    count += keys.Length;
                } catch (Exception) { }
            }

            return count;
        }
    }
}
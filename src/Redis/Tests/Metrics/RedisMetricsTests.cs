using System;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Redis.Metrics;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Redis.Tests.Metrics {
    public class RedisMetricsTests : CaptureTests {
        public RedisMetricsTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output) { }

        [Fact]
        public async Task CanIncrementCounter() {
            var metrics = new RedisMetricsClient(SharedConnection.GetMuxer());
            FlushAll();

            await metrics.CounterAsync("c1");
            Assert.Equal(1, await metrics.GetCountAsync("c1", DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow));

            await metrics.CounterAsync("c1", 5);
            Assert.Equal(6, await metrics.GetCountAsync("c1", DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow));

            await metrics.GaugeAsync("g1", 2.534);
            Assert.Equal(2.534, await metrics.GetGaugeValueAsync("g1"));

            await metrics.GaugeAsync("g1", 5.34);
            Assert.Equal(5.34, await metrics.GetGaugeValueAsync("g1"));

            await metrics.TimerAsync("t1", 50788);
            var stats = await metrics.GetTimerAsync("t1", DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow);
            Assert.Equal(1, stats.Count);

            await metrics.TimerAsync("t1", 98);
            await metrics.TimerAsync("t1", 102);
            stats = await metrics.GetTimerAsync("t1", DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow);
            Assert.Equal(3, stats.Count);
            Assert.Equal(50788 + 98 + 102, stats.TotalDuration);
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
                        _output.WriteLine(key);
                    count += keys.Length;
                } catch (Exception) { }
            }

            return count;
        }
    }
}
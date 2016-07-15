using System;
using System.Threading.Tasks;
using Foundatio.Logging;
using Foundatio.Metrics;
using Foundatio.Tests.Metrics;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Redis.Tests.Metrics {
    public class RedisMetricsTests : MetricsClientTestBase, IDisposable {
        public RedisMetricsTests(ITestOutputHelper output) : base(output) {
            FlushAll();
        }

        public override IMetricsClient GetMetricsClient(bool buffered = false) {
            return new RedisMetricsClient(SharedConnection.GetMuxer(), buffered, loggerFactory: Log);
        }

        [Fact]
        public override Task CanSetGaugesAsync() {
            return base.CanSetGaugesAsync();
        }

        [Fact]
        public override Task CanIncrementCounter() {
            return base.CanIncrementCounter();
        }

        [Fact]
        public override Task CanWaitForCounter() {
            return base.CanWaitForCounter();
        }

        [Fact]
        public override Task CanGetBufferedQueueMetrics() {
            return base.CanGetBufferedQueueMetrics();
        }

        [Fact]
        public override Task CanIncrementBufferedCounter() {
            return base.CanIncrementBufferedCounter();
        }

        [Fact]
        public override Task CanSendBufferedMetrics() {
            return base.CanSendBufferedMetrics();
        }

        public void Dispose() {
            FlushAll();
        }

        private void FlushAll() {
            var endpoints = SharedConnection.GetMuxer().GetEndPoints(true);
            if (endpoints.Length == 0)
                return;

            foreach (var endpoint in endpoints) {
                var server = SharedConnection.GetMuxer().GetServer(endpoint);

                try {
                    server.FlushAllDatabases();
                } catch (Exception ex) {
                    _logger.Error(ex, "Error flushing redis");
                }
            }
        }
    }
}
using System;
using System.Threading.Tasks;
using Foundatio.Metrics;
using Foundatio.Redis.Tests.Extensions;
using Foundatio.Tests.Metrics;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Redis.Tests.Metrics {
    public class RedisMetricsTests : MetricsClientTestBase, IDisposable {
        public RedisMetricsTests(ITestOutputHelper output) : base(output) {
            var muxer = SharedConnection.GetMuxer();
            muxer.FlushAllAsync().GetAwaiter().GetResult();
        }

        public override IMetricsClient GetMetricsClient(bool buffered = false) {
            return new RedisMetricsClient(new RedisMetricsClientOptions { ConnectionMultiplexer = SharedConnection.GetMuxer(), Buffered = buffered, LoggerFactory = Log });
        }

        [Fact]
        public override Task CanSetGaugesAsync() {
            return base.CanSetGaugesAsync();
        }

        [Fact]
        public override Task CanIncrementCounterAsync() {
            return base.CanIncrementCounterAsync();
        }

        [Fact]
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

        public void Dispose() {
            var muxer = SharedConnection.GetMuxer();
            muxer.FlushAllAsync().GetAwaiter().GetResult();
        }
    }
}
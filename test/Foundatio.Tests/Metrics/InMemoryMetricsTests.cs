using System;
using System.Threading.Tasks;
using Foundatio.Logging;
using Foundatio.Metrics;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Metrics {
    public class InMemoryMetricsTests : MetricsClientTestBase {
        public InMemoryMetricsTests(ITestOutputHelper output) : base(output) { }

        public override IMetricsClient GetMetricsClient(bool buffered = false) {
            return new InMemoryMetricsClient(buffered, loggerFactory: Log);
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
            using (TestSystemClock.Install()) {
                return base.CanWaitForCounter();
            }
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
    }
}
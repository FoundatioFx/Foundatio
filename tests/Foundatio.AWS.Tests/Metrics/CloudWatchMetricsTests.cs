using System;
using System.Threading.Tasks;
using Amazon;
using Foundatio.Metrics;
using Foundatio.Tests.Metrics;
using Foundatio.Tests.Utility;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.AWS.Tests.Metrics {
    public class CloudWatchMetricsTests : MetricsClientTestBase {
        public CloudWatchMetricsTests(ITestOutputHelper output) : base(output) {
            Log.MinimumLevel = LogLevel.Trace;
        }

        public override IMetricsClient GetMetricsClient(bool buffered = false) {
            return null;
            
            // ReSharper disable once HeuristicUnreachableCode
            string id = Guid.NewGuid().ToString("N").Substring(0, 10);
            return new CloudWatchMetricsClient(
                o => o.ConnectionString($"serviceurl=http://localhost:4566;Test Id={id}")
                    .Prefix("foundatio/tests/metrics")
                    .Buffered(buffered)
                    .LoggerFactory(Log));
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
    }
}
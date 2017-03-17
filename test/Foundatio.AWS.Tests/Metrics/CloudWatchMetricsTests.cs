using System;
using System.Threading.Tasks;
using Amazon;
using Amazon.Runtime;
using Foundatio.Metrics;
using Foundatio.Tests.Metrics;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.AWS.Tests.Metrics {
    public class CloudWatchMetricsTests : MetricsClientTestBase {
        public CloudWatchMetricsTests(ITestOutputHelper output) : base(output) {
            Log.MinimumLevel = Logging.LogLevel.Trace;
        }

        public override IMetricsClient GetMetricsClient(bool buffered = false) {
            // Don't run this as part of the tests because it doesn't work reliably since CloudWatch can take a long time for the stats to show up.
            // Also, you can't delete metrics so we have to use random ids and it creates a bunch of junk data.
            return null;

            var section = Configuration.GetSection("AWS");
            string accessKey = section["ACCESS_KEY_ID"];
            string secretKey = section["SECRET_ACCESS_KEY"];
            if (String.IsNullOrEmpty(accessKey) || String.IsNullOrEmpty(secretKey))
                return null;

            string id = Guid.NewGuid().ToString("N").Substring(0, 10);
            return new CloudWatchMetricsClient(new BasicAWSCredentials(accessKey, secretKey), RegionEndpoint.USEast1, "foundatio/tests/metrics", id, buffered, Log);
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
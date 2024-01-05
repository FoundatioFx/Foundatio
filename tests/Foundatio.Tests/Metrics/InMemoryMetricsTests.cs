using System.Threading.Tasks;
using Foundatio.Metrics;
using Foundatio.Utility;
using Foundatio.Xunit;

using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Metrics
{
    public class InMemoryMetricsTests : MetricsClientTestBase
    {
        public InMemoryMetricsTests(ITestOutputHelper output) : base(output) { }

        public override IMetricsClient GetMetricsClient(bool buffered = false)
        {
            return new InMemoryMetricsClient(o => o.LoggerFactory(Log).Buffered(buffered));
        }

        [Fact]
        public override Task CanSetGaugesAsync()
        {
            return base.CanSetGaugesAsync();
        }

        [Fact]
        public override Task CanIncrementCounterAsync()
        {
            return base.CanIncrementCounterAsync();
        }

        [RetryFact]
        public override Task CanWaitForCounterAsync()
        {
            using (TestSystemClock.Install())
            {
                return base.CanWaitForCounterAsync();
            }
        }

        [Fact]
        public override Task CanGetBufferedQueueMetricsAsync()
        {
            return base.CanGetBufferedQueueMetricsAsync();
        }

        [Fact]
        public override Task CanIncrementBufferedCounterAsync()
        {
            return base.CanIncrementBufferedCounterAsync();
        }

        [Fact]
        public override Task CanSendBufferedMetricsAsync()
        {
            return base.CanSendBufferedMetricsAsync();
        }
    }
}

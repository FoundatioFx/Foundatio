using System;
using System.Threading.Tasks;
using Foundatio.Queues;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Jobs {
    public class InMemoryJobQueueTests : JobQueueTestsBase {
        public InMemoryJobQueueTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output) {}

        protected override IQueue<SampleQueueWorkItem> GetSampleWorkItemQueue(int retries, TimeSpan retryDelay) {
            return new InMemoryQueue<SampleQueueWorkItem>(retries, retryDelay);
        }

        [Fact]
        public override Task CanRunMultipleQueueJobs() {
            return base.CanRunMultipleQueueJobs();
        }

        [Fact]
        public override Task CanRunQueueJob() {
            return base.CanRunQueueJob();
        }
    }
}
using System;
using System.Threading.Tasks;
using Foundatio.Queues;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Jobs {
    public class InMemoryJobQueueTests : JobQueueTestsBase {
        public InMemoryJobQueueTests(ITestOutputHelper output) : base(output) {}

        protected override IQueue<SampleQueueWorkItem> GetSampleWorkItemQueue(int retries, TimeSpan retryDelay) {
            return new InMemoryQueue<SampleQueueWorkItem>(retries, retryDelay, loggerFactory: Log);
        }

        [Fact]
        public override Task CanRunMultipleQueueJobs() {
            return base.CanRunMultipleQueueJobs();
        }

        [Fact]
        public override Task CanRunQueueJobWithLockFail() {
            return base.CanRunQueueJobWithLockFail();
        }

        [Fact]
        public override Task CanRunQueueJob() {
            return base.CanRunQueueJob();
        }
    }
}
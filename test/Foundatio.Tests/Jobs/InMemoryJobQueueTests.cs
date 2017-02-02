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
        public override Task CanRunMultipleQueueJobsAsync() {
            return base.CanRunMultipleQueueJobsAsync();
        }

        [Fact]
        public override Task CanRunQueueJobWithLockFailAsync() {
            return base.CanRunQueueJobWithLockFailAsync();
        }

        [Fact]
        public override Task CanRunQueueJobAsync() {
            return base.CanRunQueueJobAsync();
        }
    }
}
using System;
using System.Threading.Tasks;
using Foundatio.Queues;
using Foundatio.Tests.Jobs;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Redis.Tests.Jobs {
    public class RedisJobQueueTests : JobQueueTestsBase {
        public RedisJobQueueTests(ITestOutputHelper output) : base(output) {}

        protected override IQueue<SampleQueueWorkItem> GetSampleWorkItemQueue(int retries, TimeSpan retryDelay) {
            return new RedisQueue<SampleQueueWorkItem>(SharedConnection.GetMuxer(), retries: retries, retryDelay: retryDelay);
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
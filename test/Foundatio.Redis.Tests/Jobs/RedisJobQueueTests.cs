using System;
using System.Threading.Tasks;
using Foundatio.Queues;
using Foundatio.Redis.Tests.Extensions;
using Foundatio.Tests.Jobs;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Redis.Tests.Jobs {
    public class RedisJobQueueTests : JobQueueTestsBase {
        public RedisJobQueueTests(ITestOutputHelper output) : base(output) {
            var muxer = SharedConnection.GetMuxer();
            muxer.FlushAllAsync().GetAwaiter().GetResult();
        }

        protected override IQueue<SampleQueueWorkItem> GetSampleWorkItemQueue(int retries, TimeSpan retryDelay) {
            var muxer = SharedConnection.GetMuxer();
            return new RedisQueue<SampleQueueWorkItem>(muxer, retries: retries, retryDelay: retryDelay, loggerFactory: Log);
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
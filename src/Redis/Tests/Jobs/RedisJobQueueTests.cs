using System;
using System.Threading.Tasks;
using Foundatio.Logging;
using Foundatio.Queues;
using Foundatio.Tests.Jobs;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Redis.Tests.Jobs {
    public class RedisJobQueueTests : JobQueueTestsBase {
        public RedisJobQueueTests(ITestOutputHelper output) : base(output) {
            FlushAll();
        }

        protected override IQueue<SampleQueueWorkItem> GetSampleWorkItemQueue(int retries, TimeSpan retryDelay) {
            return new RedisQueue<SampleQueueWorkItem>(SharedConnection.GetMuxer(), retries: retries, retryDelay: retryDelay, loggerFactory: Log);
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
        
        private void FlushAll() {
            var endpoints = SharedConnection.GetMuxer().GetEndPoints(true);
            if (endpoints.Length == 0)
                return;

            foreach (var endpoint in endpoints) {
                var server = SharedConnection.GetMuxer().GetServer(endpoint);

                try {
                    server.FlushAllDatabases();
                } catch (Exception ex) {
                    _logger.Error(ex, "Error flushing redis");
                }
            }
        }
    }
}
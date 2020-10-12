using System;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Queues;
using Foundatio.Xunit;
using Microsoft.Extensions.Logging;

using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Jobs {
    public class InMemoryJobQueueTests : JobQueueTestsBase {
        public InMemoryJobQueueTests(ITestOutputHelper output) : base(output) {}

        protected override IQueue<SampleQueueWorkItem> GetSampleWorkItemQueue(int retries, TimeSpan retryDelay) {
            return new InMemoryQueue<SampleQueueWorkItem>(o => o.RetryDelay(retryDelay).Retries(retries).LoggerFactory(Log));
        }

        [Fact]
        public override Task CanRunMultipleQueueJobsAsync() {
            return base.CanRunMultipleQueueJobsAsync();
        }
        
        [Fact]
        public override Task CanRunQueueWithFailingItems() {
            return base.CanRunQueueWithFailingItems();
        }

        [RetryFact]
        public override Task CanRunQueueJobWithLockFailAsync() {
            Log.SetLogLevel<InMemoryCacheClient>(LogLevel.Trace);

            return base.CanRunQueueJobWithLockFailAsync();
        }

        [Fact]
        public override Task CanRunQueueJobAsync() {
            return base.CanRunQueueJobAsync();
        }
    }
}
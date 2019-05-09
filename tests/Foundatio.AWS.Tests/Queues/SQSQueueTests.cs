using System;
using System.Threading.Tasks;
using Foundatio.Queues;
using Foundatio.Tests.Queue;
using Foundatio.Tests.Utility;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.AWS.Tests.Queues {
    public class SQSQueueTests : QueueTestBase {
        private readonly string _queueName = "foundatio-" + Guid.NewGuid().ToString("N").Substring(10);

        public SQSQueueTests(ITestOutputHelper output) : base(output) {}

        protected override IQueue<SimpleWorkItem> GetQueue(int retries = 1, TimeSpan? workItemTimeout = null, TimeSpan? retryDelay = null, int deadLetterMaxItems = 100, bool runQueueMaintenance = true) {
            // Don't run this as part of the tests yet
            return null;

#pragma warning disable CS0162 // Unreachable code detected
            var section = Configuration.GetSection("AWS");
#pragma warning restore CS0162 // Unreachable code detected
            string accessKey = section["ACCESS_KEY_ID"];
            string secretKey = section["SECRET_ACCESS_KEY"];
            if (String.IsNullOrEmpty(accessKey) || String.IsNullOrEmpty(secretKey))
                return null;

            var queue = new SQSQueue<SimpleWorkItem>(
                o => o.Credentials(accessKey, secretKey)
                    .Name(_queueName).Retries(retries)
                    .WorkItemTimeout(workItemTimeout.GetValueOrDefault(TimeSpan.FromMinutes(5)))
                    .LoggerFactory(Log));

            _logger.LogDebug("Queue Id: {queueId}", queue.QueueId);
            return queue;
        }

        [Fact]
        public void RetryBackoff() {
            var options = new SQSQueueOptions<SimpleWorkItem>();
            var backoff1 = options.RetryDelay(1);
            Assert.InRange(backoff1, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
            var backoff2 = options.RetryDelay(2);
            Assert.InRange(backoff2, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5));
            var backoff3 = options.RetryDelay(3);
            Assert.InRange(backoff3, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(9));
            var backoff4 = options.RetryDelay(4);
            Assert.InRange(backoff4, TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(17));
            var backoff5 = options.RetryDelay(5);
            Assert.InRange(backoff5, TimeSpan.FromSeconds(32), TimeSpan.FromSeconds(33));
            var backoff6 = options.RetryDelay(6);
            Assert.InRange(backoff6, TimeSpan.FromSeconds(64), TimeSpan.FromSeconds(65));
            var backoff7 = options.RetryDelay(7);
            Assert.InRange(backoff7, TimeSpan.FromSeconds(128), TimeSpan.FromSeconds(129));
            var backoff8 = options.RetryDelay(8);
            Assert.InRange(backoff8, TimeSpan.FromSeconds(256), TimeSpan.FromSeconds(257));
            var backoff9 = options.RetryDelay(9);
            Assert.InRange(backoff9, TimeSpan.FromSeconds(512), TimeSpan.FromSeconds(513));
            var backoff10 = options.RetryDelay(10);
            Assert.InRange(backoff10, TimeSpan.FromSeconds(1024), TimeSpan.FromSeconds(1025));
        }

        [Fact]
        public override async Task CanQueueAndDequeueWorkItemAsync() {
            await base.CanQueueAndDequeueWorkItemAsync().ConfigureAwait(false);
        }

        [Fact]
        public override async Task CanDequeueWithCancelledTokenAsync() {
            await base.CanDequeueWithCancelledTokenAsync();
        }

        [Fact]
        public override async Task CanQueueAndDequeueMultipleWorkItemsAsync() {
            await base.CanQueueAndDequeueMultipleWorkItemsAsync();
        }

        [Fact]
        public override async Task WillWaitForItemAsync() {
            await base.WillWaitForItemAsync();
        }

        [Fact]
        public override async Task DequeueWaitWillGetSignaledAsync() {
            await base.DequeueWaitWillGetSignaledAsync();
        }

        [Fact]
        public override async Task CanUseQueueWorkerAsync() {
            await base.CanUseQueueWorkerAsync();
        }

        [Fact]
        public override async Task CanHandleErrorInWorkerAsync() {
            await base.CanHandleErrorInWorkerAsync();
        }

        [Fact]
        public override async Task WorkItemsWillTimeoutAsync() {
            await base.WorkItemsWillTimeoutAsync();
        }

        [Fact]
        public override async Task WorkItemsWillGetMovedToDeadletterAsync() {
            await base.WorkItemsWillGetMovedToDeadletterAsync();
        }

        [Fact]
        public override async Task CanAutoCompleteWorkerAsync() {
            await base.CanAutoCompleteWorkerAsync();
        }

        [Fact]
        public override async Task CanHaveMultipleQueueInstancesAsync() {
            await base.CanHaveMultipleQueueInstancesAsync();
        }

        [Fact]
        public override async Task CanRunWorkItemWithMetricsAsync() {
            await base.CanRunWorkItemWithMetricsAsync();
        }

        [Fact]
        public override async Task CanRenewLockAsync() {
            await base.CanRenewLockAsync();
        }

        [Fact]
        public override async Task CanAbandonQueueEntryOnceAsync() {
            await base.CanAbandonQueueEntryOnceAsync();
        }

        [Fact]
        public override async Task CanCompleteQueueEntryOnceAsync() {
            await base.CanCompleteQueueEntryOnceAsync();
        }

        public override void Dispose() {
            // do nothing
        }
    }
}

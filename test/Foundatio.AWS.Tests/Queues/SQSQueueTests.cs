using System;
using System.Threading.Tasks;
using Amazon.Runtime;
using Foundatio.Logging;
using Foundatio.Queues;
using Foundatio.Tests.Queue;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.AWS.Tests.Queues {
    public class SQSQueueTests : QueueTestBase {
        private readonly string _queueName = "foundatio-" + Guid.NewGuid().ToString("N");

        public SQSQueueTests(ITestOutputHelper output) : base(output) {
            Log.MinimumLevel = LogLevel.Trace;
        }

        protected override IQueue<SimpleWorkItem> GetQueue(int retries = 1, TimeSpan? workItemTimeout = null, TimeSpan? retryDelay = null, int deadLetterMaxItems = 100, bool runQueueMaintenance = true) {
            // skip tests for now
            return null;

            var section = Configuration.GetSection("AWS");
            string accessKey = section["ACCESS_KEY_ID"];
            string secretKey = section["SECRET_ACCESS_KEY"];
            if (String.IsNullOrEmpty(accessKey) || String.IsNullOrEmpty(secretKey))
                return null;

            var credentials = new BasicAWSCredentials(accessKey, secretKey);

            if (!retryDelay.HasValue)
                retryDelay = TimeSpan.Zero;

            _logger.Debug("Queue Id: {queueId}", _queueName);

            return new SQSQueue<SimpleWorkItem>(
                _queueName,
                credentials,
                workItemTimeout: workItemTimeout,
                loggerFactory: Log);
        }

        protected override async Task CleanupQueue(IQueue<SimpleWorkItem> queue) {
            if (queue == null)
                return;

            try {
                await queue.DeleteQueueAsync();
            } catch (Exception ex) {
                // don't throw on cleanup errror
                _logger.Error(ex, () => $"Cleanup Error: {ex.Message}");
            }
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

        public override async Task CanHandleErrorInWorkerAsync() {
            await base.CanHandleErrorInWorkerAsync();
        }

        [Fact]
        public override async Task WorkItemsWillTimeoutAsync() {
            await base.WorkItemsWillTimeoutAsync();
        }

        public override async Task WorkItemsWillGetMovedToDeadletterAsync() {
            await base.WorkItemsWillGetMovedToDeadletterAsync();
        }

        [Fact]
        public override async Task CanAutoCompleteWorkerAsync() {
            await base.CanAutoCompleteWorkerAsync();
        }


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

        // NOTE: Not using this test because you can set specific delay times for storage queue
        public override async Task CanDelayRetryAsync() {
            await base.CanDelayRetryAsync();
        }

        public override void Dispose() {
            // do nothing
        }
    }
}

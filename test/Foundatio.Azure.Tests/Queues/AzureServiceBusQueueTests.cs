using System;
using Foundatio.Queues;
using Foundatio.Tests.Queue;
using Foundatio.Tests.Utility;
using Xunit;
using System.Threading.Tasks;
using Foundatio.Logging;
using Microsoft.ServiceBus;
using Xunit.Abstractions;

namespace Foundatio.Azure.Tests.Queue {
    public class AzureServiceBusQueueTests : QueueTestBase {
        private static readonly string _queueName = Guid.NewGuid().ToString("N");

        public AzureServiceBusQueueTests(ITestOutputHelper output) : base(output) {}

        protected override IQueue<SimpleWorkItem> GetQueue(int retries = 1, TimeSpan? workItemTimeout = null, TimeSpan? retryDelay = null, int deadLetterMaxItems = 100, bool runQueueMaintenance = true) {
            if (String.IsNullOrEmpty(Configuration.GetConnectionString("ServiceBusConnectionString")))
                return null;

            if (!retryDelay.HasValue)
                retryDelay = TimeSpan.Zero;

            var maxBackoff = retryDelay.Value > TimeSpan.Zero
                ? retryDelay.Value + retryDelay.Value
                : TimeSpan.FromSeconds(1);
            var retryPolicy = new RetryExponential(retryDelay.Value, maxBackoff, retries + 1);

            _logger.Debug("Queue Id: {queueId}", _queueName);
            return new AzureServiceBusQueue<SimpleWorkItem>(Configuration.GetConnectionString("ServiceBusConnectionString"),
                _queueName, retries, workItemTimeout, retryPolicy, loggerFactory: Log);
        }

        [Fact]
        public override Task CanQueueAndDequeueWorkItem() {
            return base.CanQueueAndDequeueWorkItem();
        }
        
        [Fact]
        public override Task CanDequeueWithCancelledToken() {
            return base.CanDequeueWithCancelledToken();
        }

        [Fact]
        public override Task CanQueueAndDequeueMultipleWorkItems() {
            return base.CanQueueAndDequeueMultipleWorkItems();
        }

        [Fact]
        public override Task WillWaitForItem() {
            return base.WillWaitForItem();
        }

        [Fact]
        public override Task DequeueWaitWillGetSignaled() {
            return base.DequeueWaitWillGetSignaled();
        }

        [Fact]
        public override Task CanUseQueueWorker() {
            return base.CanUseQueueWorker();
        }

        [Fact]
        public override Task CanHandleErrorInWorker() {
            return base.CanHandleErrorInWorker();
        }

        [Fact]
        public override Task WorkItemsWillTimeout() {
            return base.WorkItemsWillTimeout();
        }

        [Fact]
        public override Task WorkItemsWillGetMovedToDeadletter() {
            return base.WorkItemsWillGetMovedToDeadletter();
        }

        [Fact]
        public override Task CanAutoCompleteWorker() {
            return base.CanAutoCompleteWorker();
        }

        [Fact]
        public override Task CanHaveMultipleQueueInstances() {
            return base.CanHaveMultipleQueueInstances();
        }

        [Fact]
        public override Task CanRunWorkItemWithMetrics() {
            return base.CanRunWorkItemWithMetrics();
        }

        [Fact]
        public override Task CanRenewLock() {
            return base.CanRenewLock();
        }

        [Fact]
        public override Task CanAbandonQueueEntryOnce() {
            return base.CanAbandonQueueEntryOnce();
        }

        [Fact]
        public override Task CanCompleteQueueEntryOnce() {
            return base.CanCompleteQueueEntryOnce();
        }

        // NOTE: Not using this test because you can set specific delay times for servicebus
        public override Task CanDelayRetry() {
            return base.CanDelayRetry();
        }
    }
}
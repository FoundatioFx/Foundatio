using System;
using Foundatio.Queues;
using Foundatio.Tests.Queue;
using Foundatio.Tests.Utility;
using Xunit;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage.RetryPolicies;
using Xunit.Abstractions;

namespace Foundatio.Azure.Tests.Queue {
    [Collection("AzureStorageIntegrationTests")]
    public class AzureStorageQueueTests : QueueTestBase {
        private static readonly string QueueName = Guid.NewGuid().ToString("N");

        public AzureStorageQueueTests(ITestOutputHelper output) : base(output) { }

        protected override IQueue<SimpleWorkItem> GetQueue(int retries = 1, TimeSpan? workItemTimeout = null, TimeSpan? retryDelay = null, int deadLetterMaxItems = 100, bool runQueueMaintenance = true) {
            if (String.IsNullOrEmpty(ConnectionStrings.Get("StorageConnectionString")))
                return null;

            if (!retryDelay.HasValue)
                retryDelay = TimeSpan.Zero;
            
            return new AzureStorageQueue<SimpleWorkItem>(
                ConnectionStrings.Get("StorageConnectionString"),
                QueueName,
                retries,
                workItemTimeout,
                TimeSpan.FromMilliseconds(50),
                new ExponentialRetry(retryDelay.Value, retries),
                loggerFactory: Log
            );
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

        // NOTE: Not using this test because you can set specific delay times for storage queue
        public override Task CanDelayRetry() {
            return base.CanDelayRetry();
        }
    }
}
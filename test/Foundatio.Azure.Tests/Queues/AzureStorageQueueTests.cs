using System;
using System.Threading.Tasks;
using Foundatio.Logging;
using Foundatio.Queues;
using Foundatio.Tests.Queue;
using Foundatio.Tests.Utility;
using Microsoft.WindowsAzure.Storage.RetryPolicies;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Azure.Tests.Queue {
    [Collection("AzureStorageIntegrationTests")]
    public class AzureStorageQueueTests : QueueTestBase {
        private static readonly string _queueName = Guid.NewGuid().ToString("N");

        public AzureStorageQueueTests(ITestOutputHelper output) : base(output) {}

        protected override IQueue<SimpleWorkItem> GetQueue(int retries = 1, TimeSpan? workItemTimeout = null, TimeSpan? retryDelay = null, int deadLetterMaxItems = 100, bool runQueueMaintenance = true) {
            string connectionString = Configuration.GetConnectionString("AzureStorageConnectionString");
            if (String.IsNullOrEmpty(connectionString))
                return null;

            _logger.Debug("Queue Id: {queueId}", _queueName);
            return new AzureStorageQueue<SimpleWorkItem>(new AzureStorageQueueOptions<SimpleWorkItem> {
                ConnectionString = connectionString,
                Name = _queueName,
                Retries = retries,
                RetryPolicy = new ExponentialRetry(retryDelay.GetValueOrDefault(TimeSpan.FromMinutes(1)), retries),
                WorkItemTimeout = workItemTimeout.GetValueOrDefault(TimeSpan.FromMinutes(5)),
                DequeueInterval = TimeSpan.FromMilliseconds(50),
                LoggerFactory = Log
            });
        }

        [Fact]
        public override Task CanQueueAndDequeueWorkItemAsync() {
            return base.CanQueueAndDequeueWorkItemAsync();
        }

        [Fact]
        public override Task CanDequeueWithCancelledTokenAsync() {
            return base.CanDequeueWithCancelledTokenAsync();
        }

        [Fact]
        public override Task CanQueueAndDequeueMultipleWorkItemsAsync() {
            return base.CanQueueAndDequeueMultipleWorkItemsAsync();
        }

        [Fact]
        public override Task WillWaitForItemAsync() {
            return base.WillWaitForItemAsync();
        }

        [Fact]
        public override Task DequeueWaitWillGetSignaledAsync() {
            return base.DequeueWaitWillGetSignaledAsync();
        }

        [Fact]
        public override Task CanUseQueueWorkerAsync() {
            return base.CanUseQueueWorkerAsync();
        }

        [Fact]
        public override Task CanHandleErrorInWorkerAsync() {
            return base.CanHandleErrorInWorkerAsync();
        }

        [Fact]
        public override Task WorkItemsWillTimeoutAsync() {
            return base.WorkItemsWillTimeoutAsync();
        }

        [Fact]
        public override Task WorkItemsWillGetMovedToDeadletterAsync() {
            return base.WorkItemsWillGetMovedToDeadletterAsync();
        }

        [Fact]
        public override Task CanAutoCompleteWorkerAsync() {
            return base.CanAutoCompleteWorkerAsync();
        }

        [Fact]
        public override Task CanHaveMultipleQueueInstancesAsync() {
            return base.CanHaveMultipleQueueInstancesAsync();
        }

        [Fact]
        public override Task CanRunWorkItemWithMetricsAsync() {
            return base.CanRunWorkItemWithMetricsAsync();
        }

        [Fact]
        public override Task CanRenewLockAsync() {
            return base.CanRenewLockAsync();
        }

        [Fact]
        public override Task CanAbandonQueueEntryOnceAsync() {
            return base.CanAbandonQueueEntryOnceAsync();
        }

        [Fact]
        public override Task CanCompleteQueueEntryOnceAsync() {
            return base.CanCompleteQueueEntryOnceAsync();
        }

        // NOTE: Not using this test because you can set specific delay times for storage queue
        public override Task CanDelayRetryAsync() {
            return base.CanDelayRetryAsync();
        }
    }
}
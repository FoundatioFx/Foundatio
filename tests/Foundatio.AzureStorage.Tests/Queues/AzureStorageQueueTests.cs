using System;
using System.Threading.Tasks;
using Foundatio.Queues;
using Foundatio.Tests.Queue;
using Foundatio.Tests.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage.RetryPolicies;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Azure.Tests.Queue {
    [Collection("AzureStorageIntegrationTests")]
    public class AzureStorageQueueTests : QueueTestBase {
        private readonly string _queueName = "foundatio-" + Guid.NewGuid().ToString("N").Substring(10);

        public AzureStorageQueueTests(ITestOutputHelper output) : base(output) {}

        protected override IQueue<SimpleWorkItem> GetQueue(int retries = 1, TimeSpan? workItemTimeout = null, TimeSpan? retryDelay = null, int deadLetterMaxItems = 100, bool runQueueMaintenance = true) {
            string connectionString = Configuration.GetConnectionString("AzureStorageConnectionString");
            if (String.IsNullOrEmpty(connectionString))
                return null;

            if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("Queue Id: {Name}", _queueName));
            return new AzureStorageQueue<SimpleWorkItem>(new AzureStorageQueueOptions<SimpleWorkItem> {
                ConnectionString = connectionString,
                Name = _queueName,
                Retries = retries,
                RetryPolicy = retries <= 0 ? new NoRetry() : (IRetryPolicy)new ExponentialRetry(retryDelay.GetValueOrDefault(TimeSpan.FromMinutes(1)), retries),
                WorkItemTimeout = workItemTimeout.GetValueOrDefault(TimeSpan.FromMinutes(5)),
                DequeueInterval = TimeSpan.FromMilliseconds(100),
                LoggerFactory = Log
            });
        }

        protected override Task CleanupQueueAsync(IQueue<SimpleWorkItem> queue) {
            // Don't delete the queue, it's super expensive and will be cleaned up later.
            queue?.Dispose();
            return Task.CompletedTask;
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

        [Fact(Skip = "Dequeue Time takes forever")]
        public override Task WillNotWaitForItemAsync() {
            return base.WillNotWaitForItemAsync();
        }

        [Fact(Skip = "Dequeue Time takes forever")]
        public override Task CanResumeDequeueEfficientlyAsync() {
            return base.CanResumeDequeueEfficientlyAsync();
        }

        [Fact(Skip = "Dequeue Time takes forever")]
        public override Task CanDequeueEfficientlyAsync() {
            return base.CanDequeueEfficientlyAsync();
        }

        [Fact]
        public override Task CanDequeueWithLockingAsync() {
            return base.CanDequeueWithLockingAsync();
        }

        [Fact]
        public override Task CanHaveMultipleQueueInstancesWithLockingAsync() {
            return base.CanHaveMultipleQueueInstancesWithLockingAsync();
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
    }
}
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
            string connectionString = Configuration.GetConnectionString("AzureServiceBusConnectionString");
            if (String.IsNullOrEmpty(connectionString))
                return null;

            if (!retryDelay.HasValue)
                retryDelay = TimeSpan.Zero;

            var maxBackoff = retryDelay.Value > TimeSpan.Zero
                ? retryDelay.Value + retryDelay.Value
                : TimeSpan.FromSeconds(1);
            var retryPolicy = new RetryExponential(retryDelay.Value, maxBackoff, retries + 1);

            _logger.Debug("Queue Id: {queueId}", _queueName);
            return new AzureServiceBusQueue<SimpleWorkItem>(new AzureServiceBusQueueOptions<SimpleWorkItem> {
                ConnectionString = connectionString,
                Name = _queueName,
                AutoDeleteOnIdle = TimeSpan.FromMinutes(5),
                EnableBatchedOperations = true,
                EnableExpress = true,
                EnablePartitioning = true,
                SupportOrdering = false,
                Retries = retries,
                RetryPolicy = retryPolicy,
                WorkItemTimeout = workItemTimeout.GetValueOrDefault(TimeSpan.FromMinutes(5)),
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

        // NOTE: Not using this test because you can set specific delay times for servicebus
        public override Task CanDelayRetryAsync() {
            return base.CanDelayRetryAsync();
        }
    }
}
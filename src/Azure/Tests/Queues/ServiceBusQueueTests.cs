using System;
using Foundatio.Queues;
using Foundatio.Tests.Queue;
using Foundatio.Tests.Utility;
using Xunit;
using System.Threading.Tasks;
using Microsoft.ServiceBus;
using Xunit.Abstractions;

namespace Foundatio.Azure.Tests.Queue {
    public class ServiceBusQueueTests : QueueTestBase {
        private readonly static string QueueName = Guid.NewGuid().ToString("N");

        protected override IQueue<SimpleWorkItem> GetQueue(int retries = 1, TimeSpan? workItemTimeout = null, TimeSpan? retryDelay = null, int deadLetterMaxItems = 100) {
            if (ConnectionStrings.Get("ServiceBusConnectionString") == null)
                return null;

            if (!retryDelay.HasValue)
                retryDelay = TimeSpan.Zero;

            var maxBackoff = retryDelay.Value > TimeSpan.Zero
                ? retryDelay.Value + retryDelay.Value
                : TimeSpan.FromSeconds(1);
            var retryPolicy = new RetryExponential(retryDelay.Value, maxBackoff, retries + 1);
            return new ServiceBusQueue<SimpleWorkItem>(ConnectionStrings.Get("ServiceBusConnectionString"),
                QueueName, retries, workItemTimeout, false, retryPolicy);
        }

        [Fact]
        public override void CanQueueAndDequeueWorkItem() {
            base.CanQueueAndDequeueWorkItem();
        }

        [Fact]
        public override void CanQueueAndDequeueMultipleWorkItems() {
            base.CanQueueAndDequeueMultipleWorkItems();
        }

        [Fact]
        public override void WillWaitForItem() {
            base.WillWaitForItem();
        }

        [Fact]
        public override void DequeueWaitWillGetSignaled() {
            base.DequeueWaitWillGetSignaled();
        }

        [Fact]
        public override void CanUseQueueWorker() {
            base.CanUseQueueWorker();
        }

        [Fact]
        public override Task CanHandleErrorInWorker() {
            return base.CanHandleErrorInWorker();
        }

        [Fact]
        public override void WorkItemsWillTimeout() {
            base.WorkItemsWillTimeout();
        }

        [Fact]
        public override void WorkItemsWillGetMovedToDeadletter() {
            base.WorkItemsWillGetMovedToDeadletter();
        }

        [Fact]
        public override void CanAutoCompleteWorker() {
            base.CanAutoCompleteWorker();
        }

        [Fact]
        public override void CanHaveMultipleQueueInstances() {
            base.CanHaveMultipleQueueInstances();
        }

        // not using this test because you can set specific delay times for servicebus
        public override void CanDelayRetry() {
            base.CanDelayRetry();
        }

        public ServiceBusQueueTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output)
        {
        }
    }
}
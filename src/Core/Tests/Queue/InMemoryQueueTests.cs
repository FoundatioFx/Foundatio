using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Foundatio.Logging;
using Foundatio.Queues;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Queue {
    public class InMemoryQueueTests : QueueTestBase {
        private IQueue<SimpleWorkItem> _queue;

        public InMemoryQueueTests(ITestOutputHelper output) : base(output) {}

        protected override IQueue<SimpleWorkItem> GetQueue(int retries = 1, TimeSpan? workItemTimeout = null, TimeSpan? retryDelay = null, int deadLetterMaxItems = 100, bool runQueueMaintenance = true) {
            if (_queue == null)
                _queue = new InMemoryQueue<SimpleWorkItem>(retries, retryDelay, workItemTimeout: workItemTimeout, loggerFactory: Log);

            return _queue;
        }

        [Fact]
        public async Task TestAsyncEvents() {
            var q = new InMemoryQueue<SimpleWorkItem>(loggerFactory: Log);
            q.Enqueuing.AddHandler(async (sender, args) => {
                await Task.Delay(250);
                _logger.Info("First Enqueuing.");
            });
            q.Enqueuing.AddHandler(async(sender, args) => {
                await Task.Delay(250);
                _logger.Info("Second Enqueuing.");
            });
            var e1 = q.Enqueued.AddHandler(async (sender, args) => {
                await Task.Delay(250);
                _logger.Info("First.");
            });
            q.Enqueued.AddHandler(async(sender, args) => {
                await Task.Delay(250);
                _logger.Info("Second.");
            });
            var sw = Stopwatch.StartNew();
            await q.EnqueueAsync(new SimpleWorkItem());
            sw.Stop();
            _logger.Trace(sw.Elapsed.ToString());

            e1.Dispose();
            sw.Restart();
            await q.EnqueueAsync(new SimpleWorkItem());
            sw.Stop();
            _logger.Trace(sw.Elapsed.ToString());
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
        public override Task CanDequeueEfficiently() {
            return base.CanDequeueEfficiently();
        }

        [Fact]
        public override Task CanQueueAndDequeueMultipleWorkItems() {
            return base.CanQueueAndDequeueMultipleWorkItems();
        }

        [Fact]
        public override Task WillNotWaitForItem() {
            return base.WillNotWaitForItem();
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
        public override Task CanDelayRetry() {
            return base.CanDelayRetry();
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
    }
}
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Foundatio.Logging;
using Foundatio.Queues;
using Foundatio.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Queue {
    public class InMemoryQueueTests : QueueTestBase {
        private IQueue<SimpleWorkItem> _queue;

        public InMemoryQueueTests(ITestOutputHelper output) : base(output) {}

        protected override IQueue<SimpleWorkItem> GetQueue(int retries = 1, TimeSpan? workItemTimeout = null, TimeSpan? retryDelay = null, int deadLetterMaxItems = 100, bool runQueueMaintenance = true) {
            if (_queue == null)
                _queue = new InMemoryQueue<SimpleWorkItem>(retries, retryDelay, workItemTimeout: workItemTimeout, loggerFactory: Log);

            _logger.Debug("Queue Id: {queueId}", _queue.QueueId);
            return _queue;
        }

        [Fact]
        public async Task TestAsyncEvents() {
            using (var q = new InMemoryQueue<SimpleWorkItem>(loggerFactory: Log)) {
                var disposables = new List<IDisposable>(5);
                try {
                    disposables.Add(q.Enqueuing.AddHandler(async (sender, args) => {
                        await SystemClock.SleepAsync(250);
                        _logger.Info("First Enqueuing.");
                    }));
                    disposables.Add(q.Enqueuing.AddHandler(async (sender, args) => {
                        await SystemClock.SleepAsync(250);
                        _logger.Info("Second Enqueuing.");
                    }));
                    disposables.Add(q.Enqueued.AddHandler(async (sender, args) => {
                        await SystemClock.SleepAsync(250);
                        _logger.Info("First.");
                    }));
                    disposables.Add(q.Enqueued.AddHandler(async (sender, args) => {
                        await SystemClock.SleepAsync(250);
                        _logger.Info("Second.");
                    }));

                    var sw = Stopwatch.StartNew();
                    await q.EnqueueAsync(new SimpleWorkItem());
                    sw.Stop();
                    _logger.Trace("Time {0}", sw.Elapsed);
                    
                    sw.Restart();
                    await q.EnqueueAsync(new SimpleWorkItem());
                    sw.Stop();
                    _logger.Trace("Time {0}", sw.Elapsed);
                } finally {
                    foreach (var disposable in disposables)
                        disposable.Dispose();
                }
            }
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
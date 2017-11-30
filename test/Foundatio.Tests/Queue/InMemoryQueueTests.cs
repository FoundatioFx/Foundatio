using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Foundatio.Queues;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Queue {
    public class InMemoryQueueTests : QueueTestBase {
        private IQueue<SimpleWorkItem> _queue;

        public InMemoryQueueTests(ITestOutputHelper output) : base(output) {}

        protected override IQueue<SimpleWorkItem> GetQueue(int retries = 1, TimeSpan? workItemTimeout = null, TimeSpan? retryDelay = null, int deadLetterMaxItems = 100, bool runQueueMaintenance = true) {
            if (_queue == null)
                _queue = new InMemoryQueue<SimpleWorkItem>(new InMemoryQueueOptions<SimpleWorkItem> {
                    Retries = retries,
                    RetryDelay = retryDelay.GetValueOrDefault(TimeSpan.FromMinutes(1)),
                    WorkItemTimeout = workItemTimeout.GetValueOrDefault(TimeSpan.FromMinutes(5)),
                    LoggerFactory = Log
                });
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Queue Id: {QueueId}", _queue.QueueId);
            return _queue;
        }

        protected override async Task CleanupQueueAsync(IQueue<SimpleWorkItem> queue) {
            if (queue == null)
                return;

            try {
                await queue.DeleteQueueAsync();
            } catch (Exception ex) {
                _logger.LogError(ex, "Error cleaning up queue");
            }
        }

        [Fact]
        public async Task TestAsyncEvents() {
            using (var q = new InMemoryQueue<SimpleWorkItem>(new InMemoryQueueOptions<SimpleWorkItem> { LoggerFactory = Log })) {
                var disposables = new List<IDisposable>(5);
                try {
                    disposables.Add(q.Enqueuing.AddHandler(async (sender, args) => {
                        await SystemClock.SleepAsync(250);
                        _logger.LogInformation("First Enqueuing.");
                    }));
                    disposables.Add(q.Enqueuing.AddHandler(async (sender, args) => {
                        await SystemClock.SleepAsync(250);
                        _logger.LogInformation("Second Enqueuing.");
                    }));
                    disposables.Add(q.Enqueued.AddHandler(async (sender, args) => {
                        await SystemClock.SleepAsync(250);
                        _logger.LogInformation("First.");
                    }));
                    disposables.Add(q.Enqueued.AddHandler(async (sender, args) => {
                        await SystemClock.SleepAsync(250);
                        _logger.LogInformation("Second.");
                    }));

                    var sw = Stopwatch.StartNew();
                    await q.EnqueueAsync(new SimpleWorkItem());
                    sw.Stop();
                    if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Time {Elapsed:g}", sw.Elapsed);

                    sw.Restart();
                    await q.EnqueueAsync(new SimpleWorkItem());
                    sw.Stop();
                    if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Time {Elapsed:g}", sw.Elapsed);
                } finally {
                    foreach (var disposable in disposables)
                        disposable.Dispose();
                }
            }
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
        public override Task CanDequeueEfficientlyAsync() {
            return base.CanDequeueEfficientlyAsync();
        }

        [Fact]
        public override Task CanResumeDequeueEfficientlyAsync() {
            return base.CanResumeDequeueEfficientlyAsync();
        }

        [Fact]
        public override Task CanQueueAndDequeueMultipleWorkItemsAsync() {
            return base.CanQueueAndDequeueMultipleWorkItemsAsync();
        }

        [Fact]
        public override Task WillNotWaitForItemAsync() {
            return base.WillNotWaitForItemAsync();
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
            using (TestSystemClock.Install()) {
                return base.WorkItemsWillTimeoutAsync();
            }
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
        public override Task CanDelayRetryAsync() {
            return base.CanDelayRetryAsync();
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

        [Fact]
        public override Task CanDequeueWithLockingAsync() {
            return base.CanDequeueWithLockingAsync();
        }

        [Fact]
        public override Task CanHaveMultipleQueueInstancesWithLockingAsync() {
            return base.CanHaveMultipleQueueInstancesWithLockingAsync();
        }

        [Fact]
        public override Task MaintainJobNotAbandon_NotWorkTimeOutEntry() {
            return base.MaintainJobNotAbandon_NotWorkTimeOutEntry();
        }
    }
}
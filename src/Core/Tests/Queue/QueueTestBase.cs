using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Jobs;
using Foundatio.Logging;
using Foundatio.Metrics;
using Foundatio.Queues;
using Foundatio.Tests.Extensions;
using Foundatio.Tests.Logging;
using Foundatio.Tests.Utility;
using Foundatio.Utility;
using Nito.AsyncEx;
using Xunit;
using Xunit.Abstractions;
#pragma warning disable CS4014

namespace Foundatio.Tests.Queue {
    public abstract class QueueTestBase : TestWithLoggingBase {
        protected QueueTestBase(ITestOutputHelper output) : base(output) {}

        protected virtual IQueue<SimpleWorkItem> GetQueue(int retries = 1, TimeSpan? workItemTimeout = null, TimeSpan? retryDelay = null, int deadLetterMaxItems = 100, bool runQueueMaintenance = true) {
            return null;
        }

        public virtual async Task CanQueueAndDequeueWorkItem() {
            var queue = GetQueue();
            if (queue == null)
                return;

            using (queue) {
                await queue.DeleteQueueAsync();

                await queue.EnqueueAsync(new SimpleWorkItem {
                    Data = "Hello"
                });
                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Enqueued);

                var workItem = await queue.DequeueAsync(TimeSpan.Zero);
                Assert.NotNull(workItem);
                Assert.Equal("Hello", workItem.Value.Data);
                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Dequeued);

                await workItem.CompleteAsync();
                var stats = await queue.GetQueueStatsAsync();
                Assert.Equal(1, stats.Completed);
                Assert.Equal(0, stats.Queued);
            }
        }
        
        /// <summary>
        /// When a cancelled token is passed into Dequeue, it will only try to dequeue one time and then exit.
        /// </summary>
        /// <returns></returns>
        public virtual async Task CanDequeueWithCancelledToken() {
            var queue = GetQueue();
            if (queue == null)
                return;

            using (queue) {
                await queue.DeleteQueueAsync();

                await queue.EnqueueAsync(new SimpleWorkItem {
                    Data = "Hello"
                });
                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Enqueued);

                var workItem = await queue.DequeueAsync(new CancellationToken(true));
                Assert.NotNull(workItem);
                Assert.Equal("Hello", workItem.Value.Data);
                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Dequeued);

                // TODO: We should verify that only one retry occurred.
                await workItem.CompleteAsync();
                var stats = await queue.GetQueueStatsAsync();
                Assert.Equal(1, stats.Completed);
                Assert.Equal(0, stats.Queued);
            }
        }

        public virtual async Task CanDequeueEfficiently() {
            const int iterations = 100;

            var queue = GetQueue(runQueueMaintenance: false);
            if (queue == null)
                return;
            
            var metrics = new InMemoryMetricsClient();
            queue.AttachBehavior(new MetricsQueueBehavior<SimpleWorkItem>(metrics));

            using (queue) {
                await queue.DeleteQueueAsync();

                Task.Run(async () => {
                    for (int index = 0; index < iterations; index++) {
                        await Task.Delay(RandomData.GetInt(10, 50));
                        await queue.EnqueueAsync(new SimpleWorkItem {
                            Data = "Hello"
                        });
                    }
                    _logger.Trace("Done enqueuing.");
                });

                _logger.Trace("Starting dequeue loop.");
                var sw = Stopwatch.StartNew();
                for (int index = 0; index < iterations; index++) {
                    var item = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
                    Assert.NotNull(item);
                    await item.CompleteAsync();
                }
                sw.Stop();

                Assert.InRange(sw.ElapsedMilliseconds, iterations * 10, iterations * 50);
                var timing = await metrics.GetTimerStatsAsync("simpleworkitem.queuetime");
                Assert.InRange(timing.AverageDuration, 0, 25);
            }
        }

        public virtual async Task CanQueueAndDequeueMultipleWorkItems() {
            var queue = GetQueue();
            if (queue == null)
                return;

            using (queue) {
                await queue.DeleteQueueAsync();

                const int workItemCount = 25;
                for (int i = 0; i < workItemCount; i++) {
                    await queue.EnqueueAsync(new SimpleWorkItem {
                        Data = "Hello"
                    });
                }
                Assert.Equal(workItemCount, (await queue.GetQueueStatsAsync()).Queued);

                var sw = Stopwatch.StartNew();
                for (int i = 0; i < workItemCount; i++) {
                    var workItem = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
                    Assert.NotNull(workItem);
                    Assert.Equal("Hello", workItem.Value.Data);
                    await workItem.CompleteAsync();
                }
                sw.Stop();
                Trace.WriteLine(sw.Elapsed);
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));

                var stats = await queue.GetQueueStatsAsync();
                Assert.Equal(workItemCount, stats.Dequeued);
                Assert.Equal(workItemCount, stats.Completed);
                Assert.Equal(0, stats.Queued);
            }
        }

        public virtual async Task WillNotWaitForItem() {
            var queue = GetQueue();
            if (queue == null)
                return;

            using (queue) {
                await queue.DeleteQueueAsync();

                var sw = Stopwatch.StartNew();
                var workItem = await queue.DequeueAsync(TimeSpan.Zero);
                sw.Stop();
                _logger.Trace("Time {0}", sw.Elapsed);
                Assert.Null(workItem);
                Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(50));
            }
        }

        public virtual async Task WillWaitForItem() {
            var queue = GetQueue();
            if (queue == null)
                return;

            using (queue) {
                await queue.DeleteQueueAsync();

                TimeSpan timeToWait = TimeSpan.FromSeconds(1);

                var sw = Stopwatch.StartNew();
                var workItem = await queue.DequeueAsync(timeToWait);
                sw.Stop();
                _logger.Trace("Time {0}", sw.Elapsed);
                Assert.Null(workItem);
                Assert.True(sw.Elapsed > timeToWait.Subtract(TimeSpan.FromMilliseconds(100)));

                Task.Factory.StartNewDelayed(100, async () => await queue.EnqueueAsync(new SimpleWorkItem {
                    Data = "Hello"
                }));

                sw.Restart();
                workItem = await queue.DequeueAsync(timeToWait);
                Assert.NotNull(workItem);
                await workItem.CompleteAsync();
                sw.Stop();
                _logger.Trace("Time {0}", sw.Elapsed);
            }
        }

        public virtual async Task DequeueWaitWillGetSignaled() {
            var queue = GetQueue();
            if (queue == null)
                return;

            using (queue) {
                await queue.DeleteQueueAsync();
                
                Task.Factory.StartNewDelayed(250, async () => await queue.EnqueueAsync(new SimpleWorkItem {
                    Data = "Hello"
                }));

                var sw = Stopwatch.StartNew();
                var workItem = await queue.DequeueAsync(TimeSpan.FromSeconds(2));
                sw.Stop();
                Trace.WriteLine(sw.Elapsed);
                Assert.NotNull(workItem);
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
            }
        }

        public virtual async Task CanUseQueueWorker() {
            var queue = GetQueue();
            if (queue == null)
                return;

            using (queue) {
                await queue.DeleteQueueAsync();

                var resetEvent = new AsyncManualResetEvent(false);
                queue.StartWorking(async w => {
                    Assert.Equal("Hello", w.Value.Data);
                    await w.CompleteAsync();
                    resetEvent.Set();
                });

                await queue.EnqueueAsync(new SimpleWorkItem {
                    Data = "Hello"
                });

                await resetEvent.WaitAsync();
                var stats = await queue.GetQueueStatsAsync();
                Assert.Equal(1, stats.Completed);
                Assert.Equal(0, stats.Queued);
                Assert.Equal(0, stats.Errors);
            }
        }

        public virtual async Task CanHandleErrorInWorker() {
            var queue = GetQueue(retries: 0);
            if (queue == null)
                return;

            var metrics = new InMemoryMetricsClient(false);
            queue.AttachBehavior(new MetricsQueueBehavior<SimpleWorkItem>(metrics));

            using (queue) {
                await queue.DeleteQueueAsync();
				
                queue.StartWorking(w => {
                    Debug.WriteLine("WorkAction");
                    Assert.Equal("Hello", w.Value.Data);
                    throw new ApplicationException();
                });
                
                var success = await metrics.WaitForCounterAsync("simpleworkitem.hello.abandoned", async () => await queue.EnqueueAsync(new SimpleWorkItem {
                    Data = "Hello"
                }), cancellationToken: TimeSpan.FromSeconds(1).ToCancellationToken());
                await Task.Delay(10);
                Assert.True(success);

                var stats = await queue.GetQueueStatsAsync();
                Assert.Equal(0, stats.Completed);
                Assert.Equal(1, stats.Errors);
                Assert.Equal(1, stats.Deadletter);
            }
        }

        public virtual async Task WorkItemsWillTimeout() {
            var queue = GetQueue(retryDelay: TimeSpan.Zero, workItemTimeout: TimeSpan.FromMilliseconds(50));
            if (queue == null)
                return;

            using (queue) {
                await queue.DeleteQueueAsync();

                await queue.EnqueueAsync(new SimpleWorkItem {
                    Data = "Hello"
                });
                var workItem = await queue.DequeueAsync(TimeSpan.Zero);
                Assert.NotNull(workItem);
                Assert.Equal("Hello", workItem.Value.Data);

                // wait for the task to be auto abandoned

                var sw = Stopwatch.StartNew();
                workItem = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
                sw.Stop();
                _logger.Trace("Time {0}", sw.Elapsed);
                Assert.NotNull(workItem);
                await workItem.CompleteAsync();
                Assert.Equal(0, (await queue.GetQueueStatsAsync()).Queued);
            }
        }

        public virtual async Task WorkItemsWillGetMovedToDeadletter() {
            var queue = GetQueue(retryDelay: TimeSpan.Zero);
            if (queue == null)
                return;

            using (queue) {
                await queue.DeleteQueueAsync();

                await queue.EnqueueAsync(new SimpleWorkItem {
                    Data = "Hello"
                });
                var workItem = await queue.DequeueAsync(TimeSpan.Zero);
                Assert.Equal("Hello", workItem.Value.Data);
                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Dequeued);

                await workItem.AbandonAsync();
                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Abandoned);

                // work item should be retried 1 time.
                workItem = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
                Assert.NotNull(workItem);
                Assert.Equal("Hello", workItem.Value.Data);
                Assert.Equal(2, (await queue.GetQueueStatsAsync()).Dequeued);

                await workItem.AbandonAsync();

                // work item should be moved to deadletter _queue after retries.
                var stats = await queue.GetQueueStatsAsync();
                Assert.Equal(1, stats.Deadletter);
                Assert.Equal(2, stats.Abandoned);
            }
        }

        public virtual async Task CanAutoCompleteWorker() {
            var queue = GetQueue();
            if (queue == null)
                return;

            using (queue) {
                await queue.DeleteQueueAsync();

                var resetEvent = new AsyncManualResetEvent(false);
                queue.StartWorking(w => {
                    Assert.Equal("Hello", w.Value.Data);
                    resetEvent.Set();
                    return TaskHelper.Completed();
                }, true);

                await queue.EnqueueAsync(new SimpleWorkItem {
                    Data = "Hello"
                });

                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Enqueued);
                await resetEvent.WaitAsync(TimeSpan.FromSeconds(2));
                // Delay again as the resetEvent will only trigger once the handler
                // has completed, not the call to CompleteAsync()
                await Task.Delay(50);

                var stats = await queue.GetQueueStatsAsync();
                Assert.Equal(0, stats.Queued);
                Assert.Equal(1, stats.Completed);
                Assert.Equal(0, stats.Errors);
            }
        }

        public virtual async Task CanHaveMultipleQueueInstances() {
            var queue = GetQueue(retries: 0, retryDelay: TimeSpan.Zero);
            if (queue == null)
                return;

            using (queue) {
                _logger.Trace("Queue Id: {0}", queue.QueueId);
                    await queue.DeleteQueueAsync();

                const int workItemCount = 50;
                const int workerCount = 3;
                var countdown = new AsyncCountdownEvent(workItemCount);
                var info = new WorkInfo();
                var workers = new List<IQueue<SimpleWorkItem>> {queue};

                for (int i = 0; i < workerCount; i++) {
                    var q = GetQueue(retries: 0, retryDelay: TimeSpan.Zero);
                    _logger.Trace("Queue Id: {0}, I: {1}", q.QueueId, i);
                    q.StartWorking(async w => await DoWorkAsync(w, countdown, info));
                    workers.Add(q);
                }

                await Run.InParallel(workItemCount, async i => {
                        var id = await queue.EnqueueAsync(new SimpleWorkItem {
                        Data = "Hello",
                        Id = i
                        });
                    _logger.Trace("Enqueued Index: {0} Id: {1}", i, id);
                });

                await countdown.WaitAsync();
                await Task.Delay(50);
                _logger.Trace("Completed: {0} Abandoned: {1} Error: {2}",
                    info.CompletedCount,
                    info.AbandonCount,
                    info.ErrorCount);


                _logger.Info("Work Info Stats: Completed: {completed} Abandoned: {abandoned} Error: {errors}", info.CompletedCount, info.AbandonCount, info.ErrorCount);
                Assert.Equal(workItemCount, info.CompletedCount + info.AbandonCount + info.ErrorCount);
                
                // In memory queue doesn't share state.
                if (queue.GetType() == typeof (InMemoryQueue<SimpleWorkItem>)) {
                    var stats = await queue.GetQueueStatsAsync();
                    Assert.Equal(0, stats.Working);
                    Assert.Equal(0, stats.Timeouts);
                    Assert.Equal(workItemCount, stats.Enqueued);
                    Assert.Equal(workItemCount, stats.Dequeued);
                    Assert.Equal(info.CompletedCount, stats.Completed);
                    Assert.Equal(info.ErrorCount, stats.Errors);
                    Assert.Equal(info.AbandonCount, stats.Abandoned - info.ErrorCount);
                    Assert.Equal(info.AbandonCount + stats.Errors, stats.Deadletter);
                } else {
                    var workerStats = new List<QueueStats>();
                    for (int i = 0; i < workers.Count; i++) {
                        var stats = await workers[i].GetQueueStatsAsync();
                        _logger.Info("Worker#{i} Working: {working} Completed: {completed} Abandoned: {abandoned} Error: {errors} Deadletter: {deadletter}", i, stats.Working, stats.Completed, stats.Abandoned, stats.Errors, stats.Deadletter);
                        workerStats.Add(stats);
                    }

                    Assert.Equal(info.CompletedCount, workerStats.Sum(s => s.Completed));
                    Assert.Equal(info.ErrorCount, workerStats.Sum(s => s.Errors));
                    Assert.Equal(info.AbandonCount, workerStats.Sum(s => s.Abandoned) - info.ErrorCount);
                    Assert.Equal(info.AbandonCount + workerStats.Sum(s => s.Errors), (workerStats.LastOrDefault()?.Deadletter ?? 0));
                }

                workers.ForEach(w => w.Dispose());
            }
        }

        public virtual async Task CanDelayRetry() {
            var queue = GetQueue(workItemTimeout: TimeSpan.FromMilliseconds(150), retryDelay: TimeSpan.FromSeconds(1));
            if (queue == null)
                return;

            using (queue) {
                await queue.DeleteQueueAsync();

                await queue.EnqueueAsync(new SimpleWorkItem {
                    Data = "Hello"
                });

                var workItem = await queue.DequeueAsync(TimeSpan.Zero);
                Assert.NotNull(workItem);
                Assert.Equal("Hello", workItem.Value.Data);

                // wait for the task to be auto abandoned
                var sw = Stopwatch.StartNew();
                await workItem.AbandonAsync();
                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Abandoned);

                workItem = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
                sw.Stop();
                Trace.WriteLine(sw.Elapsed);
                Assert.NotNull(workItem);
                Assert.True(sw.Elapsed > TimeSpan.FromSeconds(.95));
                await workItem.CompleteAsync();
                Assert.Equal(0, (await queue.GetQueueStatsAsync()).Queued);
            }
        }

        public virtual async Task CanRunWorkItemWithMetrics() {
            var eventRaised = new ManualResetEvent(false);

            var metricsClient = new InMemoryMetricsClient(false);
            var behavior = new MetricsQueueBehavior<WorkItemData>(metricsClient, "metric");
            var queue = new InMemoryQueue<WorkItemData>(behaviors: new[] { behavior });
            queue.Completed.AddHandler((sender, e) => {
                eventRaised.Set();
                return TaskHelper.Completed();
            });

            var work = new SimpleWorkItem { Id = 1, Data = "Testing" };

            await queue.EnqueueAsync(work);
            var item = await queue.DequeueAsync();
            await item.CompleteAsync();

            Assert.True(eventRaised.WaitOne(TimeSpan.FromMinutes(1)));

            Assert.Equal(1, await metricsClient.GetCounterCountAsync("metric.workitemdata.simple.enqueued"));
            Assert.Equal(1, await metricsClient.GetCounterCountAsync("metric.workitemdata.simple.dequeued"));
            Assert.Equal(1, await metricsClient.GetCounterCountAsync("metric.workitemdata.simple.completed"));

            var queueTiming = await metricsClient.GetTimerStatsAsync("metric.workitemdata.simple.queuetime");
            Assert.True(0 < queueTiming.Count);
            var processTiming = await metricsClient.GetTimerStatsAsync("metric.workitemdata.simple.processtime");
            Assert.True(0 < processTiming.Count);
        }

        public virtual async Task CanRenewLock() {
            // Need large value to reproduce this test
            var workItemTimeout = TimeSpan.FromSeconds(1);
            // Slightly shorter than the timeout to ensure we haven't lost the lock
            var renewWait = TimeSpan.FromSeconds(workItemTimeout.TotalSeconds*.75d);

            var queue = GetQueue(retryDelay: TimeSpan.Zero, workItemTimeout: workItemTimeout);
            if (queue == null)
                return;

            using (queue) {
                await queue.DeleteQueueAsync();

                await queue.EnqueueAsync(new SimpleWorkItem {
                    Data = "Hello"
                });
                var workItem = await queue.DequeueAsync(TimeSpan.Zero);
                Assert.NotNull(workItem);
                Assert.Equal("Hello", workItem.Value.Data);

                await Task.Delay(renewWait);

                await workItem.RenewLockAsync();

                await Task.Delay(renewWait);
                
                // We shouldn't get another item here if RenewLock works.
                var nullWorkItem = await queue.DequeueAsync(TimeSpan.Zero);
                Assert.Null(nullWorkItem);
                await workItem.CompleteAsync();
                Assert.Equal(0, (await queue.GetQueueStatsAsync()).Queued);
            }
        }

        protected async Task DoWorkAsync(IQueueEntry<SimpleWorkItem> w, AsyncCountdownEvent countdown, WorkInfo info) {
            Trace.WriteLine($"Starting: {w.Value.Id}");
            Assert.Equal("Hello", w.Value.Data);

            try {
                // randomly complete, abandon or blowup.
                if (RandomData.GetBool()) {
                    Trace.WriteLine($"Completing: {w.Value.Id}");
                    await w.CompleteAsync();
                    info.IncrementCompletedCount();
                } else if (RandomData.GetBool()) {
                    Trace.WriteLine($"Abandoning: {w.Value.Id}");
                    await w.AbandonAsync();
                    info.IncrementAbandonCount();
                } else {
                    Trace.WriteLine($"Erroring: {w.Value.Id}");
                    info.IncrementErrorCount();
                    throw new ApplicationException();
                }
            } finally {
                Trace.WriteLine($"Signal {countdown.CurrentCount}");
                countdown.Signal();
            }
        }
    }

    public class WorkInfo {
        private int _abandonCount = 0;
        private int _errorCount = 0;
        private int _completedCount = 0;

        public int AbandonCount => _abandonCount;
        public int ErrorCount => _errorCount;
        public int CompletedCount => _completedCount;

        public void IncrementAbandonCount() {
            Interlocked.Increment(ref _abandonCount);
        }

        public void IncrementErrorCount() {
            Interlocked.Increment(ref _errorCount);
        }

        public void IncrementCompletedCount() {
            Interlocked.Increment(ref _completedCount);
        }
    }
}
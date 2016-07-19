using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Exceptionless;
using Foundatio.Extensions;
using Foundatio.Jobs;
using Foundatio.Logging;
using Foundatio.Logging.Xunit;
using Foundatio.Metrics;
using Foundatio.Queues;
using Foundatio.Tests.Extensions;
using Foundatio.Utility;
using Nito.AsyncEx;
using Xunit;
using Xunit.Abstractions;
#pragma warning disable CS4014

namespace Foundatio.Tests.Queue {
    public abstract class QueueTestBase : TestWithLoggingBase, IDisposable {
        protected QueueTestBase(ITestOutputHelper output) : base(output) {
            SystemClock.Reset();
        }

        protected virtual IQueue<SimpleWorkItem> GetQueue(int retries = 1, TimeSpan? workItemTimeout = null, TimeSpan? retryDelay = null, int deadLetterMaxItems = 100, bool runQueueMaintenance = true) {
            return null;
        }

        public virtual async Task CanQueueAndDequeueWorkItem() {
            var queue = GetQueue();
            if (queue == null)
                return;

            using (queue) {
                await queue.DeleteQueueAsync();
                await AssertEmptyQueueAsync(queue);

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
                await AssertEmptyQueueAsync(queue);

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
            
            using (queue) {
                await queue.DeleteQueueAsync();
                await AssertEmptyQueueAsync(queue);

                using (var metrics = new InMemoryMetricsClient()) {
                    queue.AttachBehavior(new MetricsQueueBehavior<SimpleWorkItem>(metrics));

                    Task.Run(async () => {
                                 for (int index = 0; index < iterations; index++) {
                                     await SystemClock.SleepAsync(RandomData.GetInt(10, 30));
                                     await queue.EnqueueAsync(new SimpleWorkItem { Data = "Hello" });
                                 }
                                 _logger.Trace("Done enqueuing.");
                             });

                    _logger.Trace("Starting dequeue loop.");
                    var sw = Stopwatch.StartNew();
                    for (int index = 0; index < iterations; index++) {
                        var item = await queue.DequeueAsync(TimeSpan.FromSeconds(3));
                        Assert.NotNull(item);
                        await item.CompleteAsync();
                    }
                    sw.Stop();

                    Assert.InRange(sw.ElapsedMilliseconds, iterations * 10, iterations * 50);
                    var timing = await metrics.GetTimerStatsAsync("simpleworkitem.queuetime");
                    Assert.InRange(timing.AverageDuration, 0, 25);
                }
            }
        }

        public virtual async Task CanQueueAndDequeueMultipleWorkItems() {
            var queue = GetQueue();
            if (queue == null)
                return;

            using (queue) {
                await queue.DeleteQueueAsync();
                await AssertEmptyQueueAsync(queue);

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
                _logger.Trace("Time {0}", sw.Elapsed);
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));

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
                await AssertEmptyQueueAsync(queue);

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
                await AssertEmptyQueueAsync(queue);

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
                await AssertEmptyQueueAsync(queue);

                Task.Factory.StartNewDelayed(250, async () => await queue.EnqueueAsync(new SimpleWorkItem {
                    Data = "Hello"
                }));

                var sw = Stopwatch.StartNew();
                var workItem = await queue.DequeueAsync(TimeSpan.FromSeconds(2));
                sw.Stop();
                _logger.Trace("Time {0}", sw.Elapsed);
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
                await AssertEmptyQueueAsync(queue);

                var resetEvent = new AsyncManualResetEvent(false);
                await queue.StartWorkingAsync(async w => {
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

            Log.SetLogLevel<InMemoryMetricsClient>(LogLevel.Trace);
            Log.SetLogLevel<MetricsQueueBehavior<SimpleWorkItem>>(LogLevel.Trace);
            Log.SetLogLevel<InMemoryQueue<SimpleWorkItem>>(LogLevel.Trace);

            using (queue) {
                await queue.DeleteQueueAsync();
                await AssertEmptyQueueAsync(queue);

                using (var metrics = new InMemoryMetricsClient(false, loggerFactory: Log)) {
                    queue.AttachBehavior(new MetricsQueueBehavior<SimpleWorkItem>(metrics, loggerFactory: Log));

                    await queue.StartWorkingAsync(w => {
                        Debug.WriteLine("WorkAction");
                        Assert.Equal("Hello", w.Value.Data);
                        throw new Exception();
                    });

                    await SystemClock.SleepAsync(1);
                    var success = await metrics.WaitForCounterAsync("simpleworkitem.hello.abandoned", async () => await queue.EnqueueAsync(new SimpleWorkItem {
                        Data = "Hello"
                    }), cancellationToken: TimeSpan.FromSeconds(2).ToCancellationToken());
                    Assert.True(success);
                    await SystemClock.SleepAsync(1);

                    var stats = await queue.GetQueueStatsAsync();
                    _logger.Info("Completed: {completed} Errors: {errors} Deadletter: {deadletter} Working: {working} ", stats.Completed, stats.Errors, stats.Deadletter, stats.Working);
                    Assert.Equal(0, stats.Completed);
                    Assert.Equal(1, stats.Errors);
                    Assert.Equal(1, stats.Deadletter);
                }
            }
        }

        public virtual async Task WorkItemsWillTimeout() {
            var queue = GetQueue(retryDelay: TimeSpan.Zero, workItemTimeout: TimeSpan.FromMilliseconds(50));
            if (queue == null)
                return;

            using (queue) {
                await queue.DeleteQueueAsync();
                await AssertEmptyQueueAsync(queue);

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
                await AssertEmptyQueueAsync(queue);

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
                await AssertEmptyQueueAsync(queue);

                var resetEvent = new AsyncManualResetEvent(false);
                await queue.StartWorkingAsync(w => {
                    Assert.Equal("Hello", w.Value.Data);
                    return Task.CompletedTask;
                }, true);

                using (queue.Completed.AddSyncHandler((s, e) => { resetEvent.Set(); })) {
                    await queue.EnqueueAsync(new SimpleWorkItem { Data = "Hello" });

                    Assert.Equal(1, (await queue.GetQueueStatsAsync()).Enqueued);
                    await resetEvent.WaitAsync(TimeSpan.FromSeconds(2));

                    var stats = await queue.GetQueueStatsAsync();
                    Assert.Equal(0, stats.Queued);
                    Assert.Equal(0, stats.Errors);
                    Assert.Equal(1, stats.Completed);
                }
            }
        }

        public virtual async Task CanHaveMultipleQueueInstances() {
            var queue = GetQueue(retries: 0, retryDelay: TimeSpan.Zero);
            if (queue == null)
                return;

            using (queue) {
                await queue.DeleteQueueAsync();
                await AssertEmptyQueueAsync(queue);

                const int workItemCount = 50;
                const int workerCount = 3;
                var countdown = new AsyncCountdownEvent(workItemCount);
                var info = new WorkInfo();
                var workers = new List<IQueue<SimpleWorkItem>> { queue };

                try {
                    for (int i = 0; i < workerCount; i++) {
                        var q = GetQueue(retries: 0, retryDelay: TimeSpan.Zero);
                        _logger.Trace("Queue Id: {0}, I: {1}", q.QueueId, i);
                        await q.StartWorkingAsync(async w => await DoWorkAsync(w, countdown, info));
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
                    await SystemClock.SleepAsync(50);
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
                } finally {
                    foreach (var q in workers) {
                        await q.DeleteQueueAsync();
                        q.Dispose();
                    }
                }
            }
        }

        public virtual async Task CanDelayRetry() {
            var queue = GetQueue(workItemTimeout: TimeSpan.FromMilliseconds(500), retryDelay: TimeSpan.FromSeconds(1));
            if (queue == null)
                return;

            Log.SetLogLevel<InMemoryQueue<SimpleWorkItem>>(LogLevel.Trace);

            using (queue) {
                await queue.DeleteQueueAsync();
                await AssertEmptyQueueAsync(queue);

                await queue.EnqueueAsync(new SimpleWorkItem {
                    Data = "Hello"
                });

                var workItem = await queue.DequeueAsync(TimeSpan.Zero);
                Assert.NotNull(workItem);
                Assert.Equal("Hello", workItem.Value.Data);

                var sw = Stopwatch.StartNew();
                await workItem.AbandonAsync();
                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Abandoned);

                workItem = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
                sw.Stop();
                _logger.Trace("Time {0}", sw.Elapsed);
                Assert.NotNull(workItem);
                Assert.True(sw.Elapsed > TimeSpan.FromSeconds(.95));
                await workItem.CompleteAsync();
                Assert.Equal(0, (await queue.GetQueueStatsAsync()).Queued);
            }
        }

        public virtual async Task CanRunWorkItemWithMetrics() {
            int completedCount = 0;
            //Log.MinimumLevel = LogLevel.Trace;
            //Log.SetLogLevel<ScheduledTimer>(LogLevel.Information);
            //Log.SetLogLevel<InMemoryCacheClient>(LogLevel.Information);

            using (var metricsClient = new InMemoryMetricsClient(false, loggerFactory: Log)) {
                var behavior = new MetricsQueueBehavior<WorkItemData>(metricsClient, "metric", loggerFactory: Log, reportCountsInterval: TimeSpan.Zero);
                using (var queue = new InMemoryQueue<WorkItemData>(behaviors: new[] { behavior }, loggerFactory: Log)) {
                    Func<object, CompletedEventArgs<WorkItemData>, Task> handler = (sender, e) => {
                        completedCount++;
                        return Task.CompletedTask;
                    };

                    using (queue.Completed.AddHandler(handler)) {
                        _logger.Trace("Before enqueue");
                        await queue.EnqueueAsync(new SimpleWorkItem { Id = 1, Data = "Testing" });
                        await queue.EnqueueAsync(new SimpleWorkItem { Id = 2, Data = "Testing" });
                        await queue.EnqueueAsync(new SimpleWorkItem { Id = 3, Data = "Testing" });

                        await SystemClock.SleepAsync(100);

                        _logger.Trace("Before dequeue");
                        var item = await queue.DequeueAsync();
                        await item.CompleteAsync();

                        item = await queue.DequeueAsync();
                        await item.CompleteAsync();

                        item = await queue.DequeueAsync();
                        await item.AbandonAsync();

                        _logger.Trace("Before asserts");
                        Assert.Equal(2, completedCount);

                        await SystemClock.SleepAsync(100);

                        Assert.InRange((await metricsClient.GetGaugeStatsAsync("metric.workitemdata.count")).Max, 1, 3);
                        Assert.InRange((await metricsClient.GetGaugeStatsAsync("metric.workitemdata.working")).Max, 0, 1);

                        Assert.Equal(3, await metricsClient.GetCounterCountAsync("metric.workitemdata.simple.enqueued"));
                        Assert.Equal(3, await metricsClient.GetCounterCountAsync("metric.workitemdata.enqueued"));

                        Assert.Equal(3, await metricsClient.GetCounterCountAsync("metric.workitemdata.simple.dequeued"));
                        Assert.Equal(3, await metricsClient.GetCounterCountAsync("metric.workitemdata.dequeued"));

                        Assert.Equal(2, await metricsClient.GetCounterCountAsync("metric.workitemdata.simple.completed"));
                        Assert.Equal(2, await metricsClient.GetCounterCountAsync("metric.workitemdata.completed"));

                        Assert.Equal(1, await metricsClient.GetCounterCountAsync("metric.workitemdata.simple.abandoned"));
                        Assert.Equal(1, await metricsClient.GetCounterCountAsync("metric.workitemdata.abandoned"));

                        var queueTiming = await metricsClient.GetTimerStatsAsync("metric.workitemdata.simple.queuetime");
                        Assert.Equal(3, queueTiming.Count);
                        queueTiming = await metricsClient.GetTimerStatsAsync("metric.workitemdata.queuetime");
                        Assert.Equal(3, queueTiming.Count);

                        var processTiming = await metricsClient.GetTimerStatsAsync("metric.workitemdata.simple.processtime");
                        Assert.Equal(3, processTiming.Count);
                        processTiming = await metricsClient.GetTimerStatsAsync("metric.workitemdata.processtime");
                        Assert.Equal(3, processTiming.Count);

                        var queueStats = await metricsClient.GetQueueStatsAsync("metric.workitemdata");
                        Assert.Equal(3, queueStats.Enqueued.Count);
                        Assert.Equal(3, queueStats.Dequeued.Count);
                        Assert.Equal(2, queueStats.Completed.Count);
                        Assert.Equal(1, queueStats.Abandoned.Count);
                        Assert.InRange(queueStats.Count.Max, 1, 3);
                        Assert.InRange(queueStats.Working.Max, 0, 1);

                        var subQueueStats = await metricsClient.GetQueueStatsAsync("metric.workitemdata", "simple");
                        Assert.Equal(3, subQueueStats.Enqueued.Count);
                        Assert.Equal(3, subQueueStats.Dequeued.Count);
                        Assert.Equal(2, subQueueStats.Completed.Count);
                        Assert.Equal(1, subQueueStats.Abandoned.Count);
                    }
                }
            }
        }

        public virtual async Task CanRenewLock() {
            Log.SetLogLevel<InMemoryQueue<SimpleWorkItem>>(LogLevel.Trace);

            // Need large value to reproduce this test
            var workItemTimeout = TimeSpan.FromSeconds(1);
            // Slightly shorter than the timeout to ensure we haven't lost the lock
            var renewWait = TimeSpan.FromSeconds(workItemTimeout.TotalSeconds * .25d);

            var queue = GetQueue(retryDelay: TimeSpan.Zero, workItemTimeout: workItemTimeout);
            if (queue == null)
                return;

            using (queue) {
                await queue.DeleteQueueAsync();
                await AssertEmptyQueueAsync(queue);

                await queue.EnqueueAsync(new SimpleWorkItem {
                    Data = "Hello"
                });
                var entry = await queue.DequeueAsync(TimeSpan.Zero);
                Assert.NotNull(entry);
                Assert.Equal("Hello", entry.Value.Data);

                await SystemClock.SleepAsync(renewWait);
                await entry.RenewLockAsync();
                await SystemClock.SleepAsync(renewWait);
                
                // We shouldn't get another item here if RenewLock works.
                var nullWorkItem = await queue.DequeueAsync(TimeSpan.Zero);
                Assert.Null(nullWorkItem);
                await entry.CompleteAsync();
                Assert.Equal(0, (await queue.GetQueueStatsAsync()).Queued);
            }
        }

        public virtual async Task CanAbandonQueueEntryOnce() {
            var queue = GetQueue();
            if (queue == null)
                return;

            using (queue) {
                await queue.DeleteQueueAsync();
                await AssertEmptyQueueAsync(queue);

                await queue.EnqueueAsync(new SimpleWorkItem { Data = "Hello" });
                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Enqueued);

                var workItem = await queue.DequeueAsync(TimeSpan.Zero);
                Assert.NotNull(workItem);
                Assert.Equal("Hello", workItem.Value.Data);
                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Dequeued);

                await workItem.AbandonAsync();
                await Assert.ThrowsAnyAsync<Exception>(async () => await workItem.AbandonAsync());
                await Assert.ThrowsAnyAsync<Exception>(async () => await workItem.CompleteAsync());
                await Assert.ThrowsAnyAsync<Exception>(async () => await workItem.CompleteAsync());

                var stats = await queue.GetQueueStatsAsync();
                Assert.Equal(1, stats.Abandoned);
                Assert.Equal(0, stats.Completed);
                Assert.Equal(0, stats.Deadletter);
                Assert.Equal(1, stats.Dequeued);
                Assert.Equal(1, stats.Enqueued);
                Assert.Equal(0, stats.Errors);
                Assert.InRange(stats.Queued, 0, 1);
                Assert.Equal(0, stats.Timeouts);
                Assert.Equal(0, stats.Working);

                var queueEntry = workItem as QueueEntry<SimpleWorkItem>;
                if (queueEntry != null)
                    Assert.Equal(1, queueEntry.Attempts);

                await queue.EnqueueAsync(new SimpleWorkItem { Data = "Hello" });
                workItem = await queue.DequeueAsync(TimeSpan.Zero);

                await queue.AbandonAsync(workItem);
                await Assert.ThrowsAnyAsync<Exception>(async () => await workItem.CompleteAsync());
                await Assert.ThrowsAnyAsync<Exception>(async () => await workItem.AbandonAsync());
                await Assert.ThrowsAnyAsync<Exception>(async () => await queue.AbandonAsync(workItem));
                await Assert.ThrowsAnyAsync<Exception>(async () => await queue.CompleteAsync(workItem));
            }
        }
        
        public virtual async Task CanCompleteQueueEntryOnce() {
            var queue = GetQueue();
            if (queue == null)
                return;

            using (queue) {
                await queue.DeleteQueueAsync();
                await queue.EnqueueAsync(new SimpleWorkItem { Data = "Hello" });

                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Enqueued);

                var workItem = await queue.DequeueAsync(TimeSpan.Zero);
                Assert.NotNull(workItem);
                Assert.Equal("Hello", workItem.Value.Data);
                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Dequeued);

                await workItem.CompleteAsync();
                await Assert.ThrowsAnyAsync<Exception>(async () => await workItem.CompleteAsync());
                await Assert.ThrowsAnyAsync<Exception>(async () => await workItem.AbandonAsync());
                await Assert.ThrowsAnyAsync<Exception>(async () => await workItem.AbandonAsync());
                var stats = await queue.GetQueueStatsAsync();
                Assert.Equal(0, stats.Abandoned);
                Assert.Equal(1, stats.Completed);
                Assert.Equal(0, stats.Deadletter);
                Assert.Equal(1, stats.Dequeued);
                Assert.Equal(1, stats.Enqueued);
                Assert.Equal(0, stats.Errors);
                Assert.Equal(0, stats.Queued);
                Assert.Equal(0, stats.Timeouts);
                Assert.Equal(0, stats.Working);

                var queueEntry = workItem as QueueEntry<SimpleWorkItem>;
                if (queueEntry != null)
                    Assert.Equal(1, queueEntry.Attempts);
            }
        }
        
        protected async Task DoWorkAsync(IQueueEntry<SimpleWorkItem> w, AsyncCountdownEvent countdown, WorkInfo info) {
            _logger.Trace($"Starting: {w.Value.Id}");
            Assert.Equal("Hello", w.Value.Data);

            try {
                // randomly complete, abandon or blowup.
                if (RandomData.GetBool()) {
                    _logger.Trace($"Completing: {w.Value.Id}");
                    await w.CompleteAsync();
                    info.IncrementCompletedCount();
                } else if (RandomData.GetBool()) {
                    _logger.Trace($"Abandoning: {w.Value.Id}");
                    await w.AbandonAsync();
                    info.IncrementAbandonCount();
                } else {
                    _logger.Trace($"Erroring: {w.Value.Id}");
                    info.IncrementErrorCount();
                    throw new Exception();
                }
            } finally {
                _logger.Trace($"Signal {countdown.CurrentCount}");
                countdown.Signal();
            }
        }
        
        private async Task AssertEmptyQueueAsync(IQueue<SimpleWorkItem> queue) {
            var stats = await queue.GetQueueStatsAsync();
            Assert.Equal(0, stats.Abandoned);
            Assert.Equal(0, stats.Completed);
            Assert.Equal(0, stats.Deadletter);
            Assert.Equal(0, stats.Dequeued);
            Assert.Equal(0, stats.Enqueued);
            Assert.Equal(0, stats.Errors);
            Assert.Equal(0, stats.Queued);
            Assert.Equal(0, stats.Timeouts);
            Assert.Equal(0, stats.Working);
        }
        
        public virtual async void Dispose() {
            var queue = GetQueue();
            if (queue == null)
                return;

            using (queue)
                await queue.DeleteQueueAsync();
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
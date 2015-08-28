using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Queues;
using Foundatio.Tests.Utility;
using Foundatio.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Queue {
    public abstract class QueueTestBase : CaptureTests {
        protected QueueTestBase(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output)
        {
        }

        protected virtual IQueue<SimpleWorkItem> GetQueue(int retries = 1, TimeSpan? workItemTimeout = null, TimeSpan? retryDelay = null, int deadLetterMaxItems = 100) {
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
                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Completed);
                Assert.Equal(0, (await queue.GetQueueStatsAsync()).Queued);
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

                var sw = new Stopwatch();
                sw.Start();
                for (int i = 0; i < workItemCount; i++) {
                    var workItem = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
                    Assert.NotNull(workItem);
                    Assert.Equal("Hello", workItem.Value.Data);
                    await workItem.CompleteAsync();
                }
                sw.Stop();
                Trace.WriteLine(sw.Elapsed);
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));

                Assert.Equal(workItemCount, (await queue.GetQueueStatsAsync()).Dequeued);
                Assert.Equal(workItemCount, (await queue.GetQueueStatsAsync()).Completed);
                Assert.Equal(0, (await queue.GetQueueStatsAsync()).Queued);
            }
        }

        public virtual async Task WillNotWaitForItem() {
            var queue = GetQueue();
            if (queue == null)
                return;

            using (queue) {
                await queue.DeleteQueueAsync();

                var sw = new Stopwatch();
                sw.Start();
                var workItem = await queue.DequeueAsync(TimeSpan.Zero);
                sw.Stop();
                Logger.Trace().Message("Time {0}", sw.Elapsed).Write();
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
                var sw = new Stopwatch();
                sw.Start();
                var workItem = await queue.DequeueAsync(timeToWait);
                sw.Stop();
                Logger.Trace().Message("Time {0}", sw.Elapsed).Write();
                Assert.Null(workItem);
                Assert.True(sw.Elapsed > timeToWait.Subtract(TimeSpan.FromMilliseconds(100)));

                Task.Factory.StartNewDelayed(100, async () => await queue.EnqueueAsync(new SimpleWorkItem {
                    Data = "Hello"
                }));

                sw.Reset();
                sw.Start();
                workItem = await queue.DequeueAsync(timeToWait);
                await workItem.CompleteAsync();
                sw.Stop();
                Logger.Trace().Message("Time {0}", sw.Elapsed).Write();
                Assert.NotNull(workItem);
            }
        }

        public virtual async Task DequeueWaitWillGetSignaled() {
            var queue = GetQueue();
            if (queue == null)
                return;

            using (queue) {
                await queue.DeleteQueueAsync();

                Task.Factory.StartNewDelayed(250, async () => await GetQueue().EnqueueAsync(new SimpleWorkItem {
                    Data = "Hello"
                }));

                var sw = new Stopwatch();
                sw.Start();
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

                var resetEvent = new AutoResetEvent(false);
                queue.StartWorkingAsync(async w => {
                    Assert.Equal("Hello", w.Value.Data);
                    await w.CompleteAsync();
                    resetEvent.Set();
                });

                await queue.EnqueueAsync(new SimpleWorkItem {
                    Data = "Hello"
                });

                resetEvent.WaitOne(TimeSpan.FromSeconds(5));
                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Completed);
                Assert.Equal(0, (await queue.GetQueueStatsAsync()).Queued);
                Assert.Equal(0, (await queue.GetQueueStatsAsync()).Errors);
            }
        }

        public virtual async Task CanHandleErrorInWorker() {
            var queue = GetQueue(1, retryDelay: TimeSpan.Zero);
            if (queue == null)
                return;

            using (queue) {
                await queue.DeleteQueueAsync();
                queue.StartWorkingAsync(w => {
                    Debug.WriteLine("WorkAction");
                    Assert.Equal("Hello", w.Value.Data);
                    throw new ApplicationException();
                });

                await queue.EnqueueAsync(new SimpleWorkItem {
                    Data = "Hello"
                });

                var success = await TaskHelper.DelayUntil(() => queue.GetQueueStatsAsync().Result.Errors > 0, TimeSpan.FromSeconds(5));
                Assert.True(success);
                Assert.Equal(0, (await queue.GetQueueStatsAsync()).Completed);
                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Errors);

                success = await TaskHelper.DelayUntil(() => queue.GetQueueStatsAsync().Result.Queued > 0, TimeSpan.FromSeconds(5));
                Assert.True(success);
                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Queued);
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
                var sw = new Stopwatch();
                sw.Start();
                workItem = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
                sw.Stop();
                Logger.Trace().Message("Time {0}", sw.Elapsed).Write();
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
                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Deadletter);
                Assert.Equal(2, (await queue.GetQueueStatsAsync()).Abandoned);
            }
        }

        public virtual async Task CanAutoCompleteWorker() {
            var queue = GetQueue();
            if (queue == null)
                return;

            using (queue) {
                await queue.DeleteQueueAsync();

                var resetEvent = new AutoResetEvent(false);
                queue.StartWorkingAsync(w => {
                    Assert.Equal("Hello", w.Value.Data);
                    resetEvent.Set();
                    return Task.FromResult(0);
                }, true);
                await queue.EnqueueAsync(new SimpleWorkItem {
                    Data = "Hello"
                });

                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Enqueued);
                resetEvent.WaitOne(TimeSpan.FromSeconds(5));
                Thread.Sleep(100);
                Assert.Equal(0, (await queue.GetQueueStatsAsync()).Queued);
                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Completed);
                Assert.Equal(0, (await queue.GetQueueStatsAsync()).Errors);
            }
        }

        public virtual async Task CanHaveMultipleQueueInstances() {
            for (int x = 0; x < 5; x++) {
                var queue = GetQueue(retries: 0, retryDelay: TimeSpan.Zero);
                if (queue == null)
                    return;

                using (queue) {
                    Logger.Trace().Message("Queue Id: {0}", queue.QueueId).Write();
                    await queue.DeleteQueueAsync();

                    const int workItemCount = 10;
                    const int workerCount = 3;
                    var latch = new CountdownEvent(workItemCount);
                    var info = new WorkInfo();
                    var workers = new List<IQueue<SimpleWorkItem>> {queue};

                    for (int i = 0; i < workerCount; i++) {
                        var q = GetQueue(retries: 0, retryDelay: TimeSpan.Zero);
                        Logger.Trace().Message("Queue Id: {0}", q.QueueId).Write();
                        q.StartWorkingAsync(async w => await DoWorkAsync(w, latch, info));
                        workers.Add(q);
                    }

                    Parallel.For(0, workItemCount, i => {
                        var id = queue.EnqueueAsync(new SimpleWorkItem {
                            Data = "Hello",
                            Id = i
                        }).Result;
                        Logger.Trace().Message("Enqueued Index: {0} Id: {1}", i, id).Write();
                    });

                    Assert.True(latch.Wait(TimeSpan.FromSeconds(5)));
                    Thread.Sleep(100); // needed to make sure the worker error handler has time to finish
                    Logger.Trace().Message("Completed: {0} Abandoned: {1} Error: {2}",
                        info.CompletedCount,
                        info.AbandonCount,
                        info.ErrorCount).Write();

                    for (int i = 0; i < workers.Count; i++)
                    {
                        var workerStats = await workers[i].GetQueueStatsAsync();
                        Trace.WriteLine($"Worker#{i} Completed: {workerStats.Completed} Abandoned: {workerStats.Abandoned} Error: {workerStats.Errors}");
                    }

                    Assert.Equal(workItemCount, info.CompletedCount + info.AbandonCount + info.ErrorCount);

                    var stats = (await queue.GetQueueStatsAsync());
                    // In memory queue doesn't share state.
                    if (queue.GetType() == typeof (InMemoryQueue<SimpleWorkItem>)) {
                        Assert.Equal(info.CompletedCount, stats.Completed);
                        Assert.Equal(info.AbandonCount, stats.Abandoned - stats.Errors);
                        Assert.Equal(info.ErrorCount, stats.Errors);
                    } else {
                        Assert.Equal(info.CompletedCount, workers.Sum(q => q.GetQueueStatsAsync().Result.Completed));
                        Assert.Equal(info.AbandonCount, workers.Sum(q => q.GetQueueStatsAsync().Result.Abandoned) - workers.Sum(q => q.GetQueueStatsAsync().Result.Errors));
                        Assert.Equal(info.ErrorCount, workers.Sum(q => q.GetQueueStatsAsync().Result.Errors));
                    }

                    workers.ForEach(w => w.Dispose());
                }
            }
        }

        public virtual async Task CanDelayRetry() {
            var queue = GetQueue(workItemTimeout: TimeSpan.FromMilliseconds(50), retryDelay: TimeSpan.FromSeconds(1));
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
                var sw = new Stopwatch();
                sw.Start();

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

        protected async Task DoWorkAsync(QueueEntry<SimpleWorkItem> w, CountdownEvent latch, WorkInfo info) {
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
                latch.Signal();
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

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

        public virtual void CanQueueAndDequeueWorkItem() {
            var queue = GetQueue();
            if (queue == null)
                return;

            using (queue) {
                queue.DeleteQueue();

                queue.Enqueue(new SimpleWorkItem {
                    Data = "Hello"
                });
                Assert.Equal(1, queue.GetQueueStats().Enqueued);

                var workItem = queue.Dequeue(TimeSpan.Zero);
                Assert.NotNull(workItem);
                Assert.Equal("Hello", workItem.Value.Data);
                Assert.Equal(1, queue.GetQueueStats().Dequeued);

                workItem.Complete();
                Assert.Equal(1, queue.GetQueueStats().Completed);
                Assert.Equal(0, queue.GetQueueStats().Queued);
            }
        }

        public virtual void CanQueueAndDequeueMultipleWorkItems() {
            var queue = GetQueue();
            if (queue == null)
                return;

            using (queue) {
                queue.DeleteQueue();

                const int workItemCount = 25;
                for (int i = 0; i < workItemCount; i++) {
                    queue.Enqueue(new SimpleWorkItem {
                        Data = "Hello"
                    });
                }
                Assert.Equal(workItemCount, queue.GetQueueStats().Queued);

                var sw = new Stopwatch();
                sw.Start();
                for (int i = 0; i < workItemCount; i++) {
                    var workItem = queue.Dequeue(TimeSpan.FromSeconds(5));
                    Assert.NotNull(workItem);
                    Assert.Equal("Hello", workItem.Value.Data);
                    workItem.Complete();
                }
                sw.Stop();
                Trace.WriteLine(sw.Elapsed);
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));

                Assert.Equal(workItemCount, queue.GetQueueStats().Dequeued);
                Assert.Equal(workItemCount, queue.GetQueueStats().Completed);
                Assert.Equal(0, queue.GetQueueStats().Queued);
            }
        }

        public virtual void WillNotWaitForItem()
        {
            var queue = GetQueue();
            if (queue == null)
                return;

            using (queue)
            {
                queue.DeleteQueue();

                var sw = new Stopwatch();
                sw.Start();
                var workItem = queue.Dequeue(TimeSpan.Zero);
                sw.Stop();
                Logger.Trace().Message("Time {0}", sw.Elapsed).Write();
                Assert.Null(workItem);
                Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(50));
            }
        }

        public virtual void WillWaitForItem() {
            var queue = GetQueue();
            if (queue == null)
                return;

            using (queue) {
                queue.DeleteQueue();

                TimeSpan timeToWait = TimeSpan.FromSeconds(1);
                var sw = new Stopwatch();
                sw.Start();
                var workItem = queue.Dequeue(timeToWait);
                sw.Stop();
                Logger.Trace().Message("Time {0}", sw.Elapsed).Write();
                Assert.Null(workItem);
                Assert.True(sw.Elapsed > timeToWait.Subtract(TimeSpan.FromMilliseconds(100)));

                Task.Factory.StartNewDelayed(100, () => queue.Enqueue(new SimpleWorkItem {
                    Data = "Hello"
                }));

                sw.Reset();
                sw.Start();
                workItem = queue.Dequeue(timeToWait);
                workItem.Complete();
                sw.Stop();
                Logger.Trace().Message("Time {0}", sw.Elapsed).Write();
                Assert.NotNull(workItem);
            }
        }

        public virtual void DequeueWaitWillGetSignaled() {
            var queue = GetQueue();
            if (queue == null)
                return;

            using (queue) {
                queue.DeleteQueue();

                Task.Factory.StartNewDelayed(250, () => GetQueue().Enqueue(new SimpleWorkItem {
                    Data = "Hello"
                }));

                var sw = new Stopwatch();
                sw.Start();
                var workItem = queue.Dequeue(TimeSpan.FromSeconds(2));
                sw.Stop();
                Trace.WriteLine(sw.Elapsed);
                Assert.NotNull(workItem);
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
            }
        }

        public virtual void CanUseQueueWorker() {
            var queue = GetQueue();
            if (queue == null)
                return;

            using (queue) {
                queue.DeleteQueue();

                var resetEvent = new AutoResetEvent(false);
                queue.StartWorking(w => {
                    Assert.Equal("Hello", w.Value.Data);
                    w.Complete();
                    resetEvent.Set();
                });
                queue.Enqueue(new SimpleWorkItem {
                    Data = "Hello"
                });

                resetEvent.WaitOne(TimeSpan.FromSeconds(5));
                Assert.Equal(1, queue.GetQueueStats().Completed);
                Assert.Equal(0, queue.GetQueueStats().Queued);
                Assert.Equal(0, queue.GetQueueStats().Errors);
            }
        }

        public virtual async Task CanHandleErrorInWorker() {
            var queue = GetQueue(1, retryDelay: TimeSpan.Zero);
            if (queue == null)
                return;

            using (queue) {
                queue.DeleteQueue();

                queue.StartWorking(w => {
                    Debug.WriteLine("WorkAction");
                    Assert.Equal("Hello", w.Value.Data);
                    queue.StopWorking();
                    throw new ApplicationException();
                });
                queue.Enqueue(new SimpleWorkItem {
                    Data = "Hello"
                });

                var success = await TaskHelper.DelayUntil(() => queue.GetQueueStats().Errors > 0, TimeSpan.FromSeconds(5));
                Assert.True(success);
                Assert.Equal(0, queue.GetQueueStats().Completed);
                Assert.Equal(1, queue.GetQueueStats().Errors);

                success = await TaskHelper.DelayUntil(() => queue.GetQueueStats().Queued > 0, TimeSpan.FromSeconds(5));
                Assert.True(success);
                Assert.Equal(1, queue.GetQueueStats().Queued);
            }
        }

        public virtual void WorkItemsWillTimeout() {
            var queue = GetQueue(retryDelay: TimeSpan.Zero, workItemTimeout: TimeSpan.FromMilliseconds(50));
            if (queue == null)
                return;

            using (queue) {
                queue.DeleteQueue();

                queue.Enqueue(new SimpleWorkItem {
                    Data = "Hello"
                });
                var workItem = queue.Dequeue(TimeSpan.Zero);
                Assert.NotNull(workItem);
                Assert.Equal("Hello", workItem.Value.Data);

                // wait for the task to be auto abandoned
                var sw = new Stopwatch();
                sw.Start();
                workItem = queue.Dequeue(TimeSpan.FromSeconds(5));
                sw.Stop();
                Logger.Trace().Message("Time {0}", sw.Elapsed).Write();
                Assert.NotNull(workItem);
                workItem.Complete();
                Assert.Equal(0, queue.GetQueueStats().Queued);
            }
        }

        public virtual void WorkItemsWillGetMovedToDeadletter() {
            var queue = GetQueue(retryDelay: TimeSpan.Zero);
            if (queue == null)
                return;

            using (queue) {
                queue.DeleteQueue();

                queue.Enqueue(new SimpleWorkItem {
                    Data = "Hello"
                });
                var workItem = queue.Dequeue(TimeSpan.Zero);
                Assert.Equal("Hello", workItem.Value.Data);
                Assert.Equal(1, queue.GetQueueStats().Dequeued);

                workItem.Abandon();
                Assert.Equal(1, queue.GetQueueStats().Abandoned);

                // work item should be retried 1 time.
                workItem = queue.Dequeue(TimeSpan.FromSeconds(5));
                Assert.NotNull(workItem);
                Assert.Equal("Hello", workItem.Value.Data);
                Assert.Equal(2, queue.GetQueueStats().Dequeued);

                workItem.Abandon();
                // work item should be moved to deadletter _queue after retries.
                Assert.Equal(1, queue.GetQueueStats().Deadletter);
                Assert.Equal(2, queue.GetQueueStats().Abandoned);
            }
        }

        public virtual void CanAutoCompleteWorker() {
            var queue = GetQueue();
            if (queue == null)
                return;

            using (queue) {
                queue.DeleteQueue();

                var resetEvent = new AutoResetEvent(false);
                queue.StartWorking(w => {
                    Assert.Equal("Hello", w.Value.Data);
                    resetEvent.Set();
                }, true);
                queue.Enqueue(new SimpleWorkItem {
                    Data = "Hello"
                });

                Assert.Equal(1, queue.GetQueueStats().Enqueued);
                resetEvent.WaitOne(TimeSpan.FromSeconds(5));
                Thread.Sleep(100);
                Assert.Equal(0, queue.GetQueueStats().Queued);
                Assert.Equal(1, queue.GetQueueStats().Completed);
                Assert.Equal(0, queue.GetQueueStats().Errors);
            }
        }

        public virtual void CanHaveMultipleQueueInstances() {
            for (int x = 0; x < 5; x++) {
                var queue = GetQueue(retries: 0, retryDelay: TimeSpan.Zero);
                if (queue == null)
                    return;

                using (queue) {
                    Logger.Trace().Message("Queue Id: {0}", queue.QueueId).Write();
                    queue.DeleteQueue();

                    const int workItemCount = 10;
                    const int workerCount = 3;
                    var latch = new CountdownEvent(workItemCount);
                    var info = new WorkInfo();
                    var workers = new List<IQueue<SimpleWorkItem>> {queue};

                    for (int i = 0; i < workerCount; i++) {
                        var q = GetQueue(retries: 0, retryDelay: TimeSpan.Zero);
                        Logger.Trace().Message("Queue Id: {0}", q.QueueId).Write();
                        q.StartWorking(w => DoWork(w, latch, info));
                        workers.Add(q);
                    }

                    Parallel.For(0, workItemCount, i => {
                        var id = queue.Enqueue(new SimpleWorkItem {
                            Data = "Hello",
                            Id = i
                        });
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
                        var workerStats = workers[i].GetQueueStats();
                        Trace.WriteLine($"Worker#{i} Completed: {workerStats.Completed} Abandoned: {workerStats.Abandoned} Error: {workerStats.Errors}");
                    }

                    Assert.Equal(workItemCount, info.CompletedCount + info.AbandonCount + info.ErrorCount);

                    var stats = queue.GetQueueStats();
                    // In memory queue doesn't share state.
                    if (queue.GetType() == typeof (InMemoryQueue<SimpleWorkItem>)) {
                        Assert.Equal(info.CompletedCount, stats.Completed);
                        Assert.Equal(info.AbandonCount, stats.Abandoned - stats.Errors);
                        Assert.Equal(info.ErrorCount, stats.Errors);
                    } else {
                        Assert.Equal(info.CompletedCount, workers.Sum(q => q.GetQueueStats().Completed));
                        Assert.Equal(info.AbandonCount, workers.Sum(q => q.GetQueueStats().Abandoned) - workers.Sum(q => q.GetQueueStats().Errors));
                        Assert.Equal(info.ErrorCount, workers.Sum(q => q.GetQueueStats().Errors));
                    }

                    workers.ForEach(w => w.Dispose());
                }
            }
        }

        public virtual void CanDelayRetry() {
            var queue = GetQueue(workItemTimeout: TimeSpan.FromMilliseconds(50), retryDelay: TimeSpan.FromSeconds(1));
            if (queue == null)
                return;

            using (queue) {
                queue.DeleteQueue();

                queue.Enqueue(new SimpleWorkItem {
                    Data = "Hello"
                });

                var workItem = queue.Dequeue(TimeSpan.Zero);
                Assert.NotNull(workItem);
                Assert.Equal("Hello", workItem.Value.Data);

                // wait for the task to be auto abandoned
                var sw = new Stopwatch();
                sw.Start();

                workItem.Abandon();
                Assert.Equal(1, queue.GetQueueStats().Abandoned);

                workItem = queue.Dequeue(TimeSpan.FromSeconds(5));
                sw.Stop();
                Trace.WriteLine(sw.Elapsed);
                Assert.NotNull(workItem);
                Assert.True(sw.Elapsed > TimeSpan.FromSeconds(.95));
                workItem.Complete();
                Assert.Equal(0, queue.GetQueueStats().Queued);
            }
        }

        protected void DoWork(QueueEntry<SimpleWorkItem> w, CountdownEvent latch, WorkInfo info) {
            Trace.WriteLine($"Starting: {w.Value.Id}");
            Assert.Equal("Hello", w.Value.Data);

            try {
                // randomly complete, abandon or blowup.
                if (RandomData.GetBool()) {
                    Trace.WriteLine($"Completing: {w.Value.Id}");
                    w.Complete();
                    info.IncrementCompletedCount();
                } else if (RandomData.GetBool()) {
                    Trace.WriteLine($"Abandoning: {w.Value.Id}");
                    w.Abandon();
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

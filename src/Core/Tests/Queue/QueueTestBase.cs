using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Queues;
using Foundatio.Tests.Utility;
using Foundatio.Utility;
using Xunit;

namespace Foundatio.Tests.Queue {
    public abstract class QueueTestBase {
        protected virtual IQueue<SimpleWorkItem> GetQueue(int retries = 1, TimeSpan? workItemTimeout = null, TimeSpan? retryDelay = null) {
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
                Assert.Equal(1, queue.GetQueueCount());

                var workItem = queue.Dequeue(TimeSpan.Zero);
                Assert.NotNull(workItem);
                Assert.Equal("Hello", workItem.Value.Data);
                Assert.Equal(1, queue.DequeuedCount);

                workItem.Complete();
                Assert.Equal(1, queue.CompletedCount);
                Assert.Equal(0, queue.GetQueueCount());
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
                Assert.Equal(workItemCount, queue.GetQueueCount());

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

                Assert.Equal(workItemCount, queue.DequeuedCount);
                Assert.Equal(workItemCount, queue.CompletedCount);
                Assert.Equal(0, queue.GetQueueCount());
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
                Trace.WriteLine(sw.Elapsed);
                Assert.Null(workItem);
                Assert.True(sw.Elapsed > timeToWait.Subtract(TimeSpan.FromMilliseconds(10)));

                Task.Factory.StartNewDelayed(100, () => queue.Enqueue(new SimpleWorkItem {
                    Data = "Hello"
                }));

                sw.Reset();
                sw.Start();
                workItem = queue.Dequeue(timeToWait);
                workItem.Complete();
                sw.Stop();
                Trace.WriteLine(sw.Elapsed);
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
                Assert.Equal(1, queue.CompletedCount);
                Assert.Equal(0, queue.GetQueueCount());
                Assert.Equal(0, queue.WorkerErrorCount);
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

                var success = await TaskHelper.DelayUntil(() => queue.WorkerErrorCount > 0, TimeSpan.FromSeconds(5));
                Assert.True(success);
                Assert.Equal(0, queue.CompletedCount);
                Assert.Equal(1, queue.WorkerErrorCount);

                success = await TaskHelper.DelayUntil(() => queue.GetQueueCount() > 0, TimeSpan.FromSeconds(5));
                Assert.True(success);
                Assert.Equal(1, queue.GetQueueCount());
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
                Trace.WriteLine(sw.Elapsed);
                Assert.NotNull(workItem);
                workItem.Complete();
                Assert.Equal(0, queue.GetQueueCount());
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
                Assert.Equal(1, queue.DequeuedCount);

                workItem.Abandon();
                Assert.Equal(1, queue.AbandonedCount);

                // work item should be retried 1 time.
                workItem = queue.Dequeue(TimeSpan.FromSeconds(5));
                Assert.NotNull(workItem);
                Assert.Equal("Hello", workItem.Value.Data);
                Assert.Equal(2, queue.DequeuedCount);

                workItem.Abandon();
                // work item should be moved to deadletter _queue after retries.
                Assert.Equal(1, queue.GetDeadletterCount());
                Assert.Equal(2, queue.AbandonedCount);
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

                Assert.Equal(1, queue.EnqueuedCount);
                resetEvent.WaitOne(TimeSpan.FromSeconds(5));
                Thread.Sleep(100);
                Assert.Equal(0, queue.GetQueueCount());
                Assert.Equal(1, queue.CompletedCount);
                Assert.Equal(0, queue.WorkerErrorCount);
            }
        }

        public virtual void CanHaveMultipleQueueInstances() {
            for (int x = 0; x < 5; x++) {
                var queue = GetQueue(retries: 0, retryDelay: TimeSpan.Zero);
                if (queue == null)
                    return;

                using (queue) {
                    Trace.WriteLine(String.Format("Queue Id: {0}", queue.QueueId));
                    queue.DeleteQueue();

                    const int workItemCount = 10;
                    const int workerCount = 3;
                    var latch = new CountdownEvent(workItemCount);
                    var info = new WorkInfo();
                    var workers = new List<IQueue<SimpleWorkItem>> {queue};

                    for (int i = 0; i < workerCount; i++) {
                        var q = GetQueue(retries: 0, retryDelay: TimeSpan.Zero);
                        Trace.WriteLine(String.Format("Queue Id: {0}", q.QueueId));
                        q.StartWorking(w => DoWork(w, latch, info));
                        workers.Add(q);
                    }

                    Parallel.For(0, workItemCount, i => {
                        var id = queue.Enqueue(new SimpleWorkItem {
                            Data = "Hello",
                            Id = i
                        });
                        Trace.WriteLine(String.Format("Enqueued Index: {0} Id: {1}", i, id));
                    });

                    Assert.True(latch.Wait(TimeSpan.FromSeconds(5)));
                    Thread.Sleep(100); // needed to make sure the worker error handler has time to finish
                    Trace.WriteLine(String.Format("Completed: {0} Abandoned: {1} Error: {2}",
                        info.CompletedCount,
                        info.AbandonCount,
                        info.ErrorCount));

                    for (int i = 0; i < workers.Count; i++)
                        Trace.WriteLine(String.Format("Worker#{0} Completed: {1} Abandoned: {2} Error: {3}", i,
                            workers[i].CompletedCount, workers[i].AbandonedCount, workers[i].WorkerErrorCount));

                    Assert.Equal(workItemCount, info.CompletedCount + info.AbandonCount + info.ErrorCount);

                    // In memory queue doesn't share state.
                    if (queue.GetType() == typeof (InMemoryQueue<SimpleWorkItem>)) {
                        Assert.Equal(info.CompletedCount, queue.CompletedCount);
                        Assert.Equal(info.AbandonCount, queue.AbandonedCount - queue.WorkerErrorCount);
                        Assert.Equal(info.ErrorCount, queue.WorkerErrorCount);
                    } else {
                        Assert.Equal(info.CompletedCount, workers.Sum(q => q.CompletedCount));
                        Assert.Equal(info.AbandonCount, workers.Sum(q => q.AbandonedCount) - workers.Sum(q => q.WorkerErrorCount));
                        Assert.Equal(info.ErrorCount, workers.Sum(q => q.WorkerErrorCount));
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
                Assert.Equal(1, queue.AbandonedCount);

                workItem = queue.Dequeue(TimeSpan.FromSeconds(5));
                sw.Stop();
                Trace.WriteLine(sw.Elapsed);
                Assert.NotNull(workItem);
                Assert.True(sw.Elapsed > TimeSpan.FromSeconds(.99));
                workItem.Complete();
                Assert.Equal(0, queue.GetQueueCount());
            }
        }

        protected void DoWork(QueueEntry<SimpleWorkItem> w, CountdownEvent latch, WorkInfo info) {
            Trace.WriteLine(String.Format("Starting: {0}", w.Value.Id));
            Assert.Equal("Hello", w.Value.Data);

            try {
                // randomly complete, abandon or blowup.
                if (RandomData.GetBool()) {
                    Trace.WriteLine(String.Format("Completing: {0}", w.Value.Id));
                    w.Complete();
                    info.IncrementCompletedCount();
                } else if (RandomData.GetBool()) {
                    Trace.WriteLine(String.Format("Abandoning: {0}", w.Value.Id));
                    w.Abandon();
                    info.IncrementAbandonCount();
                } else {
                    Trace.WriteLine(String.Format("Erroring: {0}", w.Value.Id));
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

        public int AbandonCount { get { return _abandonCount; } }
        public int ErrorCount { get { return _errorCount; } }
        public int CompletedCount { get { return _completedCount; } }

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

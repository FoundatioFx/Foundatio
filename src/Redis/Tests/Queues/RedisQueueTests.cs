using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Exceptionless;
using Foundatio.Metrics;
using Foundatio.Queues;
using Foundatio.Tests.Queue;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Redis.Tests.Queues {
    public class RedisQueueTests : QueueTestBase {
        private readonly TestOutputWriter _output;
        public RedisQueueTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output)
        {
            _output = new TestOutputWriter(output);
        }

        protected override IQueue<SimpleWorkItem> GetQueue(int retries = 1, TimeSpan? workItemTimeout = null, TimeSpan? retryDelay = null, int deadLetterMaxItems = 100, bool runQueueMaintenance = true) {
            var queue = new RedisQueue<SimpleWorkItem>(SharedConnection.GetMuxer(), workItemTimeout: workItemTimeout, retries: retries, retryDelay: retryDelay, deadLetterMaxItems: deadLetterMaxItems, runMaintenanceTasks: runQueueMaintenance);
            Debug.WriteLine($"Queue Id: {queue.QueueId}");
            return queue;
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
        public override void WillNotWaitForItem()
        {
            base.WillNotWaitForItem();
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
        public override void CanHandleErrorInWorker() {
            base.CanHandleErrorInWorker();
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

        [Fact]
        public override void CanDelayRetry() {
            base.CanDelayRetry();
        }

        [Fact]
        public override void CanRunWorkItemWithMetrics()
        {
            base.CanRunWorkItemWithMetrics();
        }

        [Fact]
        public void VerifyCacheKeysAreCorrect() {
            var queue = GetQueue(retries: 3, workItemTimeout: TimeSpan.FromSeconds(2), retryDelay: TimeSpan.Zero, runQueueMaintenance: false);
            if (queue == null)
                return;

            FlushAll();
            Assert.Equal(0, CountAllKeys());

            using (queue) {
                var db = SharedConnection.GetMuxer().GetDatabase();

                string id = queue.Enqueue(new SimpleWorkItem { Data = "blah", Id = 1 });
                Assert.True(db.KeyExists("q:SimpleWorkItem:" + id));
                Assert.Equal(1, db.ListLength("q:SimpleWorkItem:in"));
                Assert.True(db.KeyExists("q:SimpleWorkItem:" + id + ":enqueued"));
                Assert.Equal(3, CountAllKeys());

                _output.WriteLine("-----");

                var workItem = queue.Dequeue();
                Assert.True(db.KeyExists("q:SimpleWorkItem:" + id));
                Assert.Equal(0, db.ListLength("q:SimpleWorkItem:in"));
                Assert.Equal(1, db.ListLength("q:SimpleWorkItem:work"));
                Assert.True(db.KeyExists("q:SimpleWorkItem:" + id + ":enqueued"));
                Assert.True(db.KeyExists("q:SimpleWorkItem:" + id + ":dequeued"));
                Assert.Equal(4, CountAllKeys());

                _output.WriteLine("-----");

                workItem.Complete();
                Assert.False(db.KeyExists("q:SimpleWorkItem:" + id));
                Assert.False(db.KeyExists("q:SimpleWorkItem:" + id + ":enqueued"));
                Assert.False(db.KeyExists("q:SimpleWorkItem:" + id + ":dequeued"));
                Assert.Equal(0, db.ListLength("q:SimpleWorkItem:in"));
                Assert.Equal(0, db.ListLength("q:SimpleWorkItem:work"));
                Assert.Equal(0, CountAllKeys());
            }
        }

        [Fact]
        public void VerifyCacheKeysAreCorrectAfterAbandon() {
            var queue = GetQueue(retries: 2, workItemTimeout: TimeSpan.FromMilliseconds(100), retryDelay: TimeSpan.Zero, runQueueMaintenance: false) as RedisQueue<SimpleWorkItem>;
            if (queue == null)
                return;

            FlushAll();
            Assert.Equal(0, CountAllKeys());

            using (queue) {
                var db = SharedConnection.GetMuxer().GetDatabase();

                var id = queue.Enqueue(new SimpleWorkItem { Data = "blah", Id = 1 });
                var workItem = queue.Dequeue();
                workItem.Abandon();
                Assert.True(db.KeyExists("q:SimpleWorkItem:" + id));
                Assert.Equal(1, db.ListLength("q:SimpleWorkItem:in"));
                Assert.Equal(0, db.ListLength("q:SimpleWorkItem:work"));
                Assert.True(db.KeyExists("q:SimpleWorkItem:" + id + ":dequeued"));
                Assert.True(db.KeyExists("q:SimpleWorkItem:" + id + ":enqueued"));
                Assert.Equal(1, db.StringGet("q:SimpleWorkItem:" + id + ":attempts"));
                Assert.Equal(5, CountAllKeys());

                workItem = queue.Dequeue();
                Assert.True(db.KeyExists("q:SimpleWorkItem:" + id));
                Assert.Equal(0, db.ListLength("q:SimpleWorkItem:in"));
                Assert.Equal(1, db.ListLength("q:SimpleWorkItem:work"));
                Assert.True(db.KeyExists("q:SimpleWorkItem:" + id + ":dequeued"));
                Assert.True(db.KeyExists("q:SimpleWorkItem:" + id + ":enqueued"));
                Assert.Equal(1, db.StringGet("q:SimpleWorkItem:" + id + ":attempts"));
                Assert.Equal(5, CountAllKeys());

                // let the work item timeout
                Thread.Sleep(1000);
                queue.DoMaintenanceWork();
                Assert.True(db.KeyExists("q:SimpleWorkItem:" + id));
                Assert.Equal(1, db.ListLength("q:SimpleWorkItem:in"));
                Assert.Equal(0, db.ListLength("q:SimpleWorkItem:work"));
                Assert.True(db.KeyExists("q:SimpleWorkItem:" + id + ":dequeued"));
                Assert.True(db.KeyExists("q:SimpleWorkItem:" + id + ":enqueued"));
                Assert.Equal(2, db.StringGet("q:SimpleWorkItem:" + id + ":attempts"));
                Assert.Equal(1, queue.GetQueueStats().Timeouts);
                Assert.InRange(CountAllKeys(), 5, 6);

                // should go to deadletter now
                workItem = queue.Dequeue();
                workItem.Abandon();
                Assert.True(db.KeyExists("q:SimpleWorkItem:" + id));
                Assert.Equal(0, db.ListLength("q:SimpleWorkItem:in"));
                Assert.Equal(0, db.ListLength("q:SimpleWorkItem:work"));
                Assert.Equal(1, db.ListLength("q:SimpleWorkItem:dead"));
                Assert.True(db.KeyExists("q:SimpleWorkItem:" + id + ":dequeued"));
                Assert.True(db.KeyExists("q:SimpleWorkItem:" + id + ":enqueued"));
                Assert.Equal(3, db.StringGet("q:SimpleWorkItem:" + id + ":attempts"));
                Assert.InRange(CountAllKeys(), 5, 6);
            }
        }

        [Fact]
        public void VerifyCacheKeysAreCorrectAfterAbandonWithRetryDelay() {
            var queue = GetQueue(retries: 2, workItemTimeout: TimeSpan.FromMilliseconds(100), retryDelay: TimeSpan.FromMilliseconds(250), runQueueMaintenance: false) as RedisQueue<SimpleWorkItem>;
            if (queue == null)
                return;

            FlushAll();
            Assert.Equal(0, CountAllKeys());

            using (queue) {
                var db = SharedConnection.GetMuxer().GetDatabase();

                var id = queue.Enqueue(new SimpleWorkItem { Data = "blah", Id = 1 });
                var workItem = queue.Dequeue();
                workItem.Abandon();
                Assert.True(db.KeyExists("q:SimpleWorkItem:" + id));
                Assert.Equal(0, db.ListLength("q:SimpleWorkItem:in"));
                Assert.Equal(0, db.ListLength("q:SimpleWorkItem:work"));
                Assert.Equal(1, db.ListLength("q:SimpleWorkItem:wait"));
                Assert.True(db.KeyExists("q:SimpleWorkItem:" + id + ":dequeued"));
                Assert.True(db.KeyExists("q:SimpleWorkItem:" + id + ":enqueued"));
                Assert.Equal(1, db.StringGet("q:SimpleWorkItem:" + id + ":attempts"));
                Assert.True(db.KeyExists("q:SimpleWorkItem:" + id + ":wait"));
                Assert.Equal(6, CountAllKeys());
                Thread.Sleep(1000);

                queue.DoMaintenanceWork();
                Assert.True(db.KeyExists("q:SimpleWorkItem:" + id));
                Assert.Equal(1, db.ListLength("q:SimpleWorkItem:in"));
                Assert.Equal(0, db.ListLength("q:SimpleWorkItem:work"));
                Assert.Equal(0, db.ListLength("q:SimpleWorkItem:wait"));
                Assert.True(db.KeyExists("q:SimpleWorkItem:" + id + ":dequeued"));
                Assert.True(db.KeyExists("q:SimpleWorkItem:" + id + ":enqueued"));
                Assert.Equal(1, db.StringGet("q:SimpleWorkItem:" + id + ":attempts"));
                Assert.False(db.KeyExists("q:SimpleWorkItem:" + id + ":wait"));
                Assert.InRange(CountAllKeys(), 5, 6);

                workItem = queue.Dequeue();
                Assert.True(db.KeyExists("q:SimpleWorkItem:" + id));
                Assert.Equal(0, db.ListLength("q:SimpleWorkItem:in"));
                Assert.Equal(1, db.ListLength("q:SimpleWorkItem:work"));
                Assert.True(db.KeyExists("q:SimpleWorkItem:" + id + ":dequeued"));
                Assert.True(db.KeyExists("q:SimpleWorkItem:" + id + ":enqueued"));
                Assert.Equal(1, db.StringGet("q:SimpleWorkItem:" + id + ":attempts"));
                Assert.InRange(CountAllKeys(), 5, 6);

                workItem.Complete();
                Assert.False(db.KeyExists("q:SimpleWorkItem:" + id));
                Assert.False(db.KeyExists("q:SimpleWorkItem:" + id + ":enqueued"));
                Assert.False(db.KeyExists("q:SimpleWorkItem:" + id + ":dequeued"));
                Assert.Equal(0, db.ListLength("q:SimpleWorkItem:in"));
                Assert.Equal(0, db.ListLength("q:SimpleWorkItem:work"));
                Assert.InRange(CountAllKeys(), 0, 1);
            }
        }

        [Fact]
        public void CanTrimDeadletterItems() {
            var queue = GetQueue(retries: 0, workItemTimeout: TimeSpan.FromMilliseconds(50), deadLetterMaxItems: 3, runQueueMaintenance: false) as RedisQueue<SimpleWorkItem>;
            if (queue == null)
                return;

            FlushAll();
            Assert.Equal(0, CountAllKeys());

            using (queue) {
                var db = SharedConnection.GetMuxer().GetDatabase();
                var workItemIds = new List<string>();

                for (int i = 0; i < 10; i++) {
                    var id = queue.Enqueue(new SimpleWorkItem {Data = "blah", Id = i});
                    Trace.WriteLine(id);
                    workItemIds.Add(id);
                }

                for (int i = 0; i < 10; i++) {
                    var workItem = queue.Dequeue();
                    workItem.Abandon();
                    Trace.WriteLine("Abondoning: " + workItem.Id);
                }

                workItemIds.Reverse();
                queue.DoMaintenanceWork();

                foreach (var id in workItemIds.Take(3)) {
                    Trace.WriteLine("Checking: " + id);
                    Assert.True(db.KeyExists("q:SimpleWorkItem:" + id));
                }
                Assert.Equal(0, db.ListLength("q:SimpleWorkItem:in"));
                Assert.Equal(0, db.ListLength("q:SimpleWorkItem:work"));
                Assert.Equal(0, db.ListLength("q:SimpleWorkItem:wait"));
                Assert.Equal(3, db.ListLength("q:SimpleWorkItem:dead"));
                Assert.InRange(CountAllKeys(), 13, 14);
            }
        }
        
        // TODO: Need to write tests that verify the cache data is correct after each operation.

        [Fact]
        public void MeasureThroughputWithRandomFailures() {
            var queue = GetQueue(retries: 3, workItemTimeout: TimeSpan.FromSeconds(2), retryDelay: TimeSpan.Zero);
            if (queue == null)
                return;

            FlushAll();

            using (queue) {
                queue.DeleteQueue();

                const int workItemCount = 1000;
                for (int i = 0; i < workItemCount; i++) {
                    queue.Enqueue(new SimpleWorkItem {
                        Data = "Hello"
                    });
                }
                Assert.Equal(workItemCount, queue.GetQueueStats().Queued);

                var metrics = new InMemoryMetricsClient();
                var workItem = queue.Dequeue(TimeSpan.Zero);
                while (workItem != null) {
                    Assert.Equal("Hello", workItem.Value.Data);
                    if (RandomData.GetBool(10))
                        workItem.Abandon();
                    else
                        workItem.Complete();
                    metrics.Counter("work");

                    workItem = queue.Dequeue(TimeSpan.FromMilliseconds(100));
                }
                metrics.DisplayStats(_output);

                var stats = queue.GetQueueStats();
                Assert.True(stats.Dequeued >= workItemCount);
                Assert.Equal(workItemCount, stats.Completed + stats.Deadletter);
                Assert.Equal(0, stats.Queued);

                Trace.WriteLine(CountAllKeys());
            }
        }

        [Fact]
        public void MeasureThroughput() {
            var queue = GetQueue(retries: 3, workItemTimeout: TimeSpan.FromSeconds(2), retryDelay: TimeSpan.FromSeconds(1));
            if (queue == null)
                return;

            FlushAll();

            using (queue) {
                queue.DeleteQueue();

                const int workItemCount = 1000;
                for (int i = 0; i < workItemCount; i++) {
                    queue.Enqueue(new SimpleWorkItem {
                        Data = "Hello"
                    });
                }
                Assert.Equal(workItemCount, queue.GetQueueStats().Queued);

                var metrics = new InMemoryMetricsClient();
                var workItem = queue.Dequeue(TimeSpan.Zero);
                while (workItem != null) {
                    Assert.Equal("Hello", workItem.Value.Data);
                    workItem.Complete();
                    metrics.Counter("work");

                    workItem = queue.Dequeue(TimeSpan.Zero);
                }
                metrics.DisplayStats(_output);

                var stats = queue.GetQueueStats();
                Assert.Equal(workItemCount, stats.Dequeued);
                Assert.Equal(workItemCount, stats.Completed);
                Assert.Equal(0, stats.Queued);

                Trace.WriteLine(CountAllKeys());
            }
        }

        [Fact]
        public void MeasureWorkerThroughput() {
            var queue = GetQueue(retries: 3, workItemTimeout: TimeSpan.FromSeconds(2), retryDelay: TimeSpan.FromSeconds(1));
            if (queue == null)
                return;

            FlushAll();

            using (queue) {
                queue.DeleteQueue();

                const int workItemCount = 1000;
                for (int i = 0; i < workItemCount; i++) {
                    queue.Enqueue(new SimpleWorkItem {
                        Data = "Hello"
                    });
                }
                Assert.Equal(workItemCount, queue.GetQueueStats().Queued);

                var countdown = new CountDownLatch(workItemCount);
                var metrics = new InMemoryMetricsClient();
                queue.StartWorking(workItem => {
                    Assert.Equal("Hello", workItem.Value.Data);
                    workItem.Complete();
                    metrics.Counter("work");
                    countdown.Signal();
                });
                countdown.Wait(60 * 1000);
                metrics.DisplayStats(_output);

                var stats = queue.GetQueueStats();
                Assert.Equal(workItemCount, stats.Dequeued);
                Assert.Equal(workItemCount, stats.Completed);
                Assert.Equal(0, stats.Queued);

                Trace.WriteLine(CountAllKeys());
            }
        }

        private void FlushAll() {
            var endpoints = SharedConnection.GetMuxer().GetEndPoints(true);
            if (endpoints.Length == 0)
                return;

            foreach (var endpoint in endpoints) {
                var server = SharedConnection.GetMuxer().GetServer(endpoint);

                try {
                    server.FlushAllDatabases();
                } catch (Exception) { }
            }
        }

        private int CountAllKeys() {
            var endpoints = SharedConnection.GetMuxer().GetEndPoints(true);
            if (endpoints.Length == 0)
                return 0;

            int count = 0;
            foreach (var endpoint in endpoints) {
                var server = SharedConnection.GetMuxer().GetServer(endpoint);

                try {
                    var keys = server.Keys().ToArray();
                    foreach (var key in keys)
                        _output.WriteLine(key);
                    count += keys.Length;
                } catch (Exception) { }
            }

            return count;
        }
    }
}
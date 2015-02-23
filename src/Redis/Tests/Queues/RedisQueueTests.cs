using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Exceptionless.RandomData;
using Foundatio.Metrics;
using Foundatio.Queues;
using Foundatio.Redis.Queues;
using Foundatio.Tests.Queue;
using Foundatio.Tests.Utility;
using Xunit;

namespace Foundatio.Redis.Tests.Queues {
    public class RedisQueueTests : QueueTestBase {
        protected override IQueue<SimpleWorkItem> GetQueue(int retries = 1, TimeSpan? workItemTimeout = null, TimeSpan? retryDelay = null) {
            var queue = new RedisQueue<SimpleWorkItem>(SharedConnection.GetMuxer(), workItemTimeout: workItemTimeout, retries: retries, retryDelay: retryDelay);
            Debug.WriteLine(String.Format("Queue Id: {0}", queue.QueueId));
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

        [Fact]
        public override void CanDelayRetry() {
            base.CanDelayRetry();
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

                const int workItemCount = 10000;
                for (int i = 0; i < workItemCount; i++) {
                    queue.Enqueue(new SimpleWorkItem {
                        Data = "Hello"
                    });
                }
                Assert.Equal(workItemCount, queue.GetQueueCount());

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
                metrics.DisplayStats();

                Assert.True(queue.DequeuedCount >= workItemCount);
                Assert.Equal(workItemCount, queue.CompletedCount + queue.GetDeadletterCount());
                Assert.Equal(0, queue.GetQueueCount());

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

                const int workItemCount = 10000;
                for (int i = 0; i < workItemCount; i++) {
                    queue.Enqueue(new SimpleWorkItem {
                        Data = "Hello"
                    });
                }
                Assert.Equal(workItemCount, queue.GetQueueCount());

                var metrics = new InMemoryMetricsClient();
                var workItem = queue.Dequeue(TimeSpan.Zero);
                while (workItem != null) {
                    Assert.Equal("Hello", workItem.Value.Data);
                    workItem.Complete();
                    metrics.Counter("work");

                    workItem = queue.Dequeue(TimeSpan.Zero);
                }
                metrics.DisplayStats();

                Assert.Equal(workItemCount, queue.DequeuedCount);
                Assert.Equal(workItemCount, queue.CompletedCount);
                Assert.Equal(0, queue.GetQueueCount());

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

                const int workItemCount = 10000;
                for (int i = 0; i < workItemCount; i++) {
                    queue.Enqueue(new SimpleWorkItem {
                        Data = "Hello"
                    });
                }
                Assert.Equal(workItemCount, queue.GetQueueCount());

                var countdown = new CountDownLatch(workItemCount);
                var metrics = new InMemoryMetricsClient();
                queue.StartWorking(workItem => {
                    Assert.Equal("Hello", workItem.Value.Data);
                    workItem.Complete();
                    metrics.Counter("work");
                    countdown.Signal();
                });
                countdown.Wait(60 * 1000);
                metrics.DisplayStats();

                Assert.Equal(workItemCount, queue.DequeuedCount);
                Assert.Equal(workItemCount, queue.CompletedCount);
                Assert.Equal(0, queue.GetQueueCount());

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
                    server.FlushDatabase();
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
                    count += server.Keys().Count();
                } catch (Exception) { }
            }

            return count;
        }
    }
}
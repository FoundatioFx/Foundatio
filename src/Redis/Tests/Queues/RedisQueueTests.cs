using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Queues;
using Foundatio.Redis.Queues;
using Foundatio.Tests.Queue;
using Foundatio.Tests.Utility;
using StackExchange.Redis;
using Xunit;
using Exceptionless.RandomData;

namespace Foundatio.Redis.Tests.Queues {
    public class RedisQueueTests : QueueTestBase {
        private ConnectionMultiplexer _muxer;

        protected override IQueue<SimpleWorkItem> GetQueue(int retries = 1, TimeSpan? workItemTimeout = null, TimeSpan? retryDelay = null) {
            if (ConnectionStrings.Get("RedisConnectionString") == null)
                return null;

            if (_muxer == null)
                _muxer = ConnectionMultiplexer.Connect(ConnectionStrings.Get("RedisConnectionString"));

            var queue = new RedisQueue<SimpleWorkItem>(_muxer, workItemTimeout: workItemTimeout, retries: retries, retryDelay: retryDelay);
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

                var sw = new Stopwatch();
                sw.Start();
                var workItem = queue.Dequeue(TimeSpan.FromSeconds(5));
                while (workItem != null) {
                    Assert.Equal("Hello", workItem.Value.Data);
                    workItem.Complete();

                    workItem = queue.Dequeue(TimeSpan.FromSeconds(5));
                }
                sw.Stop();
                Trace.WriteLine("Queue throughput: " + (workItemCount / sw.Elapsed.TotalSeconds).ToString("F2") + "/sec");

                Assert.Equal(workItemCount, queue.DequeuedCount);
                Assert.Equal(workItemCount, queue.CompletedCount);
                Assert.Equal(0, queue.GetQueueCount());

                Trace.WriteLine(CountAllKeys());
            }
        }

        [Fact]
        public void CanBeatItRealGood() {
            var queue = GetQueue(retries: 3, workItemTimeout: TimeSpan.FromSeconds(2), retryDelay: TimeSpan.FromSeconds(1));
            if (queue == null)
                return;

            using (queue) {
                queue.DeleteQueue();

                const int workItemCount = 10000;
                for (int i = 0; i < workItemCount; i++) {
                    queue.Enqueue(new SimpleWorkItem {
                        Data = "Hello"
                    });
                }
                Assert.Equal(workItemCount, queue.GetQueueCount());

                var sw = new Stopwatch();
                sw.Start();
                var workItem = queue.Dequeue(TimeSpan.FromSeconds(5));
                while (workItem != null) {
                    Assert.Equal("Hello", workItem.Value.Data);

                    // randomly don't complete
                    if (RandomData.GetBool(90))
                        workItem.Complete();

                    // get next item
                    workItem = queue.Dequeue(TimeSpan.FromSeconds(5));
                }
                sw.Stop();
                Trace.WriteLine(sw.Elapsed);
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));

                Assert.Equal(workItemCount, queue.DequeuedCount);
                Assert.Equal(workItemCount, queue.CompletedCount);
                Assert.Equal(0, queue.GetQueueCount());
            }
        }

        private void FlushAll() {
            var endpoints = _muxer.GetEndPoints(true);
            if (endpoints.Length == 0)
                return;

            foreach (var endpoint in endpoints) {
                var server = _muxer.GetServer(endpoint);

                try {
                    server.FlushDatabase();
                } catch (Exception) { }
            }
        }

        private int CountAllKeys() {
            var endpoints = _muxer.GetEndPoints(true);
            if (endpoints.Length == 0)
                return 0;

            int count = 0;
            foreach (var endpoint in endpoints) {
                var server = _muxer.GetServer(endpoint);

                try {
                    count += server.Keys().Count();
                } catch (Exception) { }
            }

            return count;
        }
    }
}
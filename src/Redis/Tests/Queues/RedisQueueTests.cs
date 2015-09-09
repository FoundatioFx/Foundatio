using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Exceptionless;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Metrics;
using Foundatio.Queues;
using Foundatio.Tests.Queue;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;
#pragma warning disable 4014

namespace Foundatio.Redis.Tests.Queues {
    public class RedisQueueTests : QueueTestBase {
        private readonly TestOutputWriter _output;

        public RedisQueueTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output) {
            _output = new TestOutputWriter(output);
        }

        protected override IQueue<SimpleWorkItem> GetQueue(int retries = 1, TimeSpan? workItemTimeout = null, TimeSpan? retryDelay = null, int deadLetterMaxItems = 100, bool runQueueMaintenance = true) {
            var queue = new RedisQueue<SimpleWorkItem>(SharedConnection.GetMuxer(), workItemTimeout: workItemTimeout, retries: retries, retryDelay: retryDelay, deadLetterMaxItems: deadLetterMaxItems, runMaintenanceTasks: runQueueMaintenance);
            Logger.Debug().Message($"Queue Id: {queue.QueueId}").Write();
            return queue;
        }

        [Fact]
        public override Task CanQueueAndDequeueWorkItem() {
            return base.CanQueueAndDequeueWorkItem();
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
        public async Task VerifyCacheKeysAreCorrect() {
            var queue = GetQueue(retries: 3, workItemTimeout: TimeSpan.FromSeconds(2), retryDelay: TimeSpan.Zero, runQueueMaintenance: false);
            if (queue == null)
                return;

            FlushAll();
            Assert.Equal(0, CountAllKeys());

            using (queue) {
                var db = SharedConnection.GetMuxer().GetDatabase();

                string id = await queue.EnqueueAsync(new SimpleWorkItem { Data = "blah", Id = 1 }).AnyContext();
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id).AnyContext());
                Assert.Equal(1, await db.ListLengthAsync("q:SimpleWorkItem:in").AnyContext());
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":enqueued").AnyContext());
                Assert.Equal(3, CountAllKeys());

                _output.WriteLine("-----");

                var workItem = await queue.DequeueAsync().AnyContext();
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id).AnyContext());
                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:in").AnyContext());
                Assert.Equal(1, await db.ListLengthAsync("q:SimpleWorkItem:work").AnyContext());
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":enqueued").AnyContext());
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":dequeued").AnyContext());
                Assert.Equal(4, CountAllKeys());

                _output.WriteLine("-----");

                await workItem.CompleteAsync().AnyContext();
                Assert.False(await db.KeyExistsAsync("q:SimpleWorkItem:" + id).AnyContext());
                Assert.False(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":enqueued").AnyContext());
                Assert.False(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":dequeued").AnyContext());
                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:in").AnyContext());
                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:work").AnyContext());
                Assert.Equal(0, CountAllKeys());
            }
        }

        [Fact]
        public async Task VerifyCacheKeysAreCorrectAfterAbandon() {
            var queue = GetQueue(retries: 2, workItemTimeout: TimeSpan.FromMilliseconds(100), retryDelay: TimeSpan.Zero, runQueueMaintenance: false) as RedisQueue<SimpleWorkItem>;
            if (queue == null)
                return;

            FlushAll();
            Assert.Equal(0, CountAllKeys());

            using (queue) {
                var db = SharedConnection.GetMuxer().GetDatabase();

                var id = await queue.EnqueueAsync(new SimpleWorkItem { Data = "blah", Id = 1 }).AnyContext();
                var workItem = await queue.DequeueAsync().AnyContext();
                await workItem.AbandonAsync().AnyContext();
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id).AnyContext());
                Assert.Equal(1, await db.ListLengthAsync("q:SimpleWorkItem:in").AnyContext());
                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:work").AnyContext());
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":dequeued").AnyContext());
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":enqueued").AnyContext());
                Assert.Equal(1, await db.StringGetAsync("q:SimpleWorkItem:" + id + ":attempts").AnyContext());
                Assert.Equal(5, CountAllKeys());

                workItem = await queue.DequeueAsync().AnyContext();
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id).AnyContext());
                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:in").AnyContext());
                Assert.Equal(1, await db.ListLengthAsync("q:SimpleWorkItem:work").AnyContext());
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":dequeued").AnyContext());
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":enqueued").AnyContext());
                Assert.Equal(1, await db.StringGetAsync("q:SimpleWorkItem:" + id + ":attempts").AnyContext());
                Assert.Equal(5, CountAllKeys());

                // let the work item timeout
                await Task.Delay(1000).AnyContext();
                await queue.DoMaintenanceWorkAsync().AnyContext();
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id).AnyContext());
                Assert.Equal(1, await db.ListLengthAsync("q:SimpleWorkItem:in").AnyContext());
                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:work").AnyContext());
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":dequeued").AnyContext());
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":enqueued").AnyContext());
                Assert.Equal(2, await db.StringGetAsync("q:SimpleWorkItem:" + id + ":attempts").AnyContext());
                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Timeouts);
                Assert.InRange(CountAllKeys(), 5, 6);

                // should go to deadletter now
                workItem = await queue.DequeueAsync().AnyContext();
                await workItem.AbandonAsync().AnyContext();
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id).AnyContext());
                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:in").AnyContext());
                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:work").AnyContext());
                Assert.Equal(1, await db.ListLengthAsync("q:SimpleWorkItem:dead").AnyContext());
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":dequeued").AnyContext());
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":enqueued").AnyContext());
                Assert.Equal(3, await db.StringGetAsync("q:SimpleWorkItem:" + id + ":attempts").AnyContext());
                Assert.InRange(CountAllKeys(), 5, 6);
            }
        }

        [Fact]
        public async Task VerifyCacheKeysAreCorrectAfterAbandonWithRetryDelay() {
            var queue = GetQueue(retries: 2, workItemTimeout: TimeSpan.FromMilliseconds(100), retryDelay: TimeSpan.FromMilliseconds(250), runQueueMaintenance: false) as RedisQueue<SimpleWorkItem>;
            if (queue == null)
                return;

            FlushAll();
            Assert.Equal(0, CountAllKeys());

            using (queue) {
                var db = SharedConnection.GetMuxer().GetDatabase();

                var id = await queue.EnqueueAsync(new SimpleWorkItem { Data = "blah", Id = 1 }).AnyContext();
                var workItem = await queue.DequeueAsync().AnyContext();
                await workItem.AbandonAsync().AnyContext();
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id).AnyContext());
                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:in").AnyContext());
                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:work").AnyContext());
                Assert.Equal(1, await db.ListLengthAsync("q:SimpleWorkItem:wait").AnyContext());
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":dequeued").AnyContext());
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":enqueued").AnyContext());
                Assert.Equal(1, await db.StringGetAsync("q:SimpleWorkItem:" + id + ":attempts").AnyContext());
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":wait").AnyContext());
                Assert.Equal(6, CountAllKeys());
                await Task.Delay(1000).AnyContext();

                await queue.DoMaintenanceWorkAsync().AnyContext();
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id).AnyContext());
                Assert.Equal(1, await db.ListLengthAsync("q:SimpleWorkItem:in").AnyContext());
                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:work").AnyContext());
                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:wait").AnyContext());
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":dequeued").AnyContext());
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":enqueued").AnyContext());
                Assert.Equal(1, await db.StringGetAsync("q:SimpleWorkItem:" + id + ":attempts").AnyContext());
                Assert.False(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":wait").AnyContext());
                Assert.InRange(CountAllKeys(), 5, 6);

                workItem = await queue.DequeueAsync().AnyContext();
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id).AnyContext());
                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:in").AnyContext());
                Assert.Equal(1, await db.ListLengthAsync("q:SimpleWorkItem:work").AnyContext());
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":dequeued").AnyContext());
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":enqueued").AnyContext());
                Assert.Equal(1, await db.StringGetAsync("q:SimpleWorkItem:" + id + ":attempts").AnyContext());
                Assert.InRange(CountAllKeys(), 5, 6);

                await workItem.CompleteAsync().AnyContext();
                Assert.False(await db.KeyExistsAsync("q:SimpleWorkItem:" + id).AnyContext());
                Assert.False(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":enqueued").AnyContext());
                Assert.False(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":dequeued").AnyContext());
                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:in").AnyContext());
                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:work").AnyContext());
                Assert.InRange(CountAllKeys(), 0, 1);
            }
        }

        [Fact]
        public async Task CanTrimDeadletterItems() {
            var queue = GetQueue(retries: 0, workItemTimeout: TimeSpan.FromMilliseconds(50), deadLetterMaxItems: 3, runQueueMaintenance: false) as RedisQueue<SimpleWorkItem>;
            if (queue == null)
                return;

            FlushAll();
            Assert.Equal(0, CountAllKeys());

            using (queue) {
                var db = SharedConnection.GetMuxer().GetDatabase();
                var workItemIds = new List<string>();

                for (int i = 0; i < 10; i++) {
                    var id = await queue.EnqueueAsync(new SimpleWorkItem {Data = "blah", Id = i}).AnyContext();
                    Trace.WriteLine(id);
                    workItemIds.Add(id);
                }

                for (int i = 0; i < 10; i++) {
                    var workItem = await queue.DequeueAsync().AnyContext();
                    await workItem.AbandonAsync().AnyContext();
                    Trace.WriteLine("Abondoning: " + workItem.Id);
                }

                workItemIds.Reverse();
                await queue.DoMaintenanceWorkAsync().AnyContext();

                foreach (var id in workItemIds.Take(3)) {
                    Trace.WriteLine("Checking: " + id);
                    Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id).AnyContext());
                }

                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:in").AnyContext());
                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:work").AnyContext());
                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:wait").AnyContext());
                Assert.Equal(3, await db.ListLengthAsync("q:SimpleWorkItem:dead").AnyContext());
                Assert.InRange(CountAllKeys(), 13, 14);
            }
        }
        
        // TODO: Need to write tests that verify the cache data is correct after each operation.

        [Fact]
        public async Task MeasureThroughputWithRandomFailures() {
            var queue = GetQueue(retries: 3, workItemTimeout: TimeSpan.FromSeconds(2), retryDelay: TimeSpan.Zero);
            if (queue == null)
                return;

            FlushAll();

            using (queue) {
                await queue.DeleteQueueAsync().AnyContext();

                const int workItemCount = 1000;
                for (int i = 0; i < workItemCount; i++) {
                    await queue.EnqueueAsync(new SimpleWorkItem {
                        Data = "Hello"
                    }).AnyContext();
                }
                Assert.Equal(workItemCount, (await queue.GetQueueStatsAsync().AnyContext()).Queued);

                var metrics = new InMemoryMetricsClient();
                var workItem = await queue.DequeueAsync(TimeSpan.Zero).AnyContext();
                while (workItem != null) {
                    Assert.Equal("Hello", workItem.Value.Data);
                    if (RandomData.GetBool(10))
                        await workItem.AbandonAsync().AnyContext();
                    else
                        await workItem.CompleteAsync().AnyContext();

                    await metrics.CounterAsync("work").AnyContext();

                    workItem = await queue.DequeueAsync(TimeSpan.FromMilliseconds(100)).AnyContext();
                }
                metrics.DisplayStats(_output);

                var stats = await queue.GetQueueStatsAsync().AnyContext();
                Assert.True(stats.Dequeued >= workItemCount);
                Assert.Equal(workItemCount, stats.Completed + stats.Deadletter);
                Assert.Equal(0, stats.Queued);

                Trace.WriteLine(CountAllKeys());
            }
        }

        [Fact]
        public async Task MeasureThroughput() {
            var queue = GetQueue(retries: 3, workItemTimeout: TimeSpan.FromSeconds(2), retryDelay: TimeSpan.FromSeconds(1));
            if (queue == null)
                return;

            FlushAll();

            using (queue) {
                await queue.DeleteQueueAsync().AnyContext();

                const int workItemCount = 1000;
                for (int i = 0; i < workItemCount; i++) {
                    await queue.EnqueueAsync(new SimpleWorkItem {
                        Data = "Hello"
                    }).AnyContext();
                }
                Assert.Equal(workItemCount, (await queue.GetQueueStatsAsync().AnyContext()).Queued);

                var metrics = new InMemoryMetricsClient();
                var workItem = await queue.DequeueAsync(TimeSpan.Zero).AnyContext();
                while (workItem != null) {
                    Assert.Equal("Hello", workItem.Value.Data);
                    await workItem.CompleteAsync().AnyContext();
                    await metrics.CounterAsync("work").AnyContext();

                    workItem = await queue.DequeueAsync(TimeSpan.Zero).AnyContext();
                }
                metrics.DisplayStats(_output);

                var stats = await queue.GetQueueStatsAsync().AnyContext();
                Assert.Equal(workItemCount, stats.Dequeued);
                Assert.Equal(workItemCount, stats.Completed);
                Assert.Equal(0, stats.Queued);

                Trace.WriteLine(CountAllKeys());
            }
        }

        [Fact]
        public async Task MeasureWorkerThroughput() {
            var queue = GetQueue(retries: 3, workItemTimeout: TimeSpan.FromSeconds(2), retryDelay: TimeSpan.FromSeconds(1));
            if (queue == null)
                return;

            FlushAll();

            using (queue) {
                await queue.DeleteQueueAsync().AnyContext();

                const int workItemCount = 1000;
                for (int i = 0; i < workItemCount; i++) {
                    await queue.EnqueueAsync(new SimpleWorkItem {
                        Data = "Hello"
                    }).AnyContext();
                }
                Assert.Equal(workItemCount, (await queue.GetQueueStatsAsync().AnyContext()).Queued);

                var countdown = new CountDownLatch(workItemCount);
                var metrics = new InMemoryMetricsClient();
                queue.StartWorkingAsync(async workItem => {
                    Assert.Equal("Hello", workItem.Value.Data);
                    await workItem.CompleteAsync().AnyContext();
                    await metrics.CounterAsync("work").AnyContext();
                    countdown.Signal();
                });
                countdown.Wait(60 * 1000);
                metrics.DisplayStats(_output);

                var stats = await queue.GetQueueStatsAsync().AnyContext();
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
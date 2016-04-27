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
using Nito.AsyncEx;
using Xunit;
using Xunit.Abstractions;
#pragma warning disable 4014

namespace Foundatio.Redis.Tests.Queues {
    public class RedisQueueTests : QueueTestBase {
        public RedisQueueTests(ITestOutputHelper output) : base(output) {
            FlushAll();
            Assert.Equal(0, CountAllKeys());
        }

        protected override IQueue<SimpleWorkItem> GetQueue(int retries = 1, TimeSpan? workItemTimeout = null, TimeSpan? retryDelay = null, int deadLetterMaxItems = 100, bool runQueueMaintenance = true) {
            var queue = new RedisQueue<SimpleWorkItem>(SharedConnection.GetMuxer(), workItemTimeout: workItemTimeout,
                retries: retries, retryDelay: retryDelay, deadLetterMaxItems: deadLetterMaxItems, runMaintenanceTasks: runQueueMaintenance, loggerFactory: Log);
            _logger.Debug("Queue Id: {queueId}", queue.QueueId);
            return queue;
        }

        [Fact]
        public override Task CanQueueAndDequeueWorkItem() {
            return base.CanQueueAndDequeueWorkItem();
        }

        [Fact]
        public override Task CanDequeueWithCancelledToken() {
            return base.CanDequeueWithCancelledToken();
        }

        [Fact]
        public override Task CanDequeueEfficiently() {
            return base.CanDequeueEfficiently();
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
        public override Task CanRenewLock() {
            return base.CanRenewLock();
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
        public override Task CanAbandonQueueEntryOnce() {
            return base.CanAbandonQueueEntryOnce();
        }

        [Fact]
        public override Task CanCompleteQueueEntryOnce() {
            return base.CanCompleteQueueEntryOnce();
        }

        [Fact]
        public async Task VerifyCacheKeysAreCorrect() {
            var queue = GetQueue(retries: 3, workItemTimeout: TimeSpan.FromSeconds(2), retryDelay: TimeSpan.Zero, runQueueMaintenance: false);
            if (queue == null)
                return;
            
            using (queue) {
                var db = SharedConnection.GetMuxer().GetDatabase();

                string id = await queue.EnqueueAsync(new SimpleWorkItem { Data = "blah", Id = 1 });
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id));
                Assert.Equal(1, await db.ListLengthAsync("q:SimpleWorkItem:in"));
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":enqueued"));
                Assert.Equal(3, CountAllKeys());

                _logger.Info("-----");

                var workItem = await queue.DequeueAsync();
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id));
                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:in"));
                Assert.Equal(1, await db.ListLengthAsync("q:SimpleWorkItem:work"));
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":enqueued"));
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":renewed"));
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":dequeued"));
                Assert.Equal(5, CountAllKeys());

                _logger.Info("-----");

                await workItem.CompleteAsync();
                Assert.False(await db.KeyExistsAsync("q:SimpleWorkItem:" + id));
                Assert.False(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":enqueued"));
                Assert.False(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":renewed"));
                Assert.False(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":dequeued"));
                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:in"));
                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:work"));
                Assert.Equal(0, CountAllKeys());
            }
        }

        [Fact]
        public async Task VerifyCacheKeysAreCorrectAfterAbandon() {
            var queue = GetQueue(retries: 2, workItemTimeout: TimeSpan.FromMilliseconds(100), retryDelay: TimeSpan.Zero, runQueueMaintenance: false) as RedisQueue<SimpleWorkItem>;
            if (queue == null)
                return;
            
            using (queue) {
                var db = SharedConnection.GetMuxer().GetDatabase();

                var id = await queue.EnqueueAsync(new SimpleWorkItem { Data = "blah", Id = 1 });
                var workItem = await queue.DequeueAsync();
                await workItem.AbandonAsync();
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id));
                Assert.Equal(1, await db.ListLengthAsync("q:SimpleWorkItem:in"));
                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:work"));
                Assert.False(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":dequeued"));
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":enqueued"));
                Assert.False(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":renewed"));
                Assert.Equal(1, await db.StringGetAsync("q:SimpleWorkItem:" + id + ":attempts"));
                Assert.Equal(4, CountAllKeys());

                workItem = await queue.DequeueAsync();
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id));
                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:in"));
                Assert.Equal(1, await db.ListLengthAsync("q:SimpleWorkItem:work"));
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":dequeued"));
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":enqueued"));
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":renewed"));
                Assert.Equal(1, await db.StringGetAsync("q:SimpleWorkItem:" + id + ":attempts"));
                Assert.Equal(6, CountAllKeys());

                // let the work item timeout
                await Task.Delay(1000);
                await queue.DoMaintenanceWorkAsync();
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id));
                Assert.Equal(1, await db.ListLengthAsync("q:SimpleWorkItem:in"));
                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:work"));
                Assert.False(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":dequeued"));
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":enqueued"));
                Assert.False(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":renewed"));
                Assert.Equal(2, await db.StringGetAsync("q:SimpleWorkItem:" + id + ":attempts"));
                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Timeouts);
                Assert.InRange(CountAllKeys(), 3, 4);

                // should go to deadletter now
                workItem = await queue.DequeueAsync();
                await workItem.AbandonAsync();
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id));
                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:in"));
                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:work"));
                Assert.Equal(1, await db.ListLengthAsync("q:SimpleWorkItem:dead"));
                Assert.False(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":dequeued"));
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":enqueued"));
                Assert.False(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":renewed"));
                Assert.Equal(3, await db.StringGetAsync("q:SimpleWorkItem:" + id + ":attempts"));
                Assert.InRange(CountAllKeys(), 4, 5);
            }
        }

        [Fact]
        public async Task VerifyCacheKeysAreCorrectAfterAbandonWithRetryDelay() {
            var queue = GetQueue(retries: 2, workItemTimeout: TimeSpan.FromMilliseconds(100), retryDelay: TimeSpan.FromMilliseconds(250), runQueueMaintenance: false) as RedisQueue<SimpleWorkItem>;
            if (queue == null)
                return;
            
            using (queue) {
                var db = SharedConnection.GetMuxer().GetDatabase();

                var id = await queue.EnqueueAsync(new SimpleWorkItem { Data = "blah", Id = 1 });
                var workItem = await queue.DequeueAsync();
                await workItem.AbandonAsync();
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id));
                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:in"));
                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:work"));
                Assert.Equal(1, await db.ListLengthAsync("q:SimpleWorkItem:wait"));
                Assert.False(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":dequeued"));
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":enqueued"));
                Assert.False(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":renewed"));
                Assert.Equal(1, await db.StringGetAsync("q:SimpleWorkItem:" + id + ":attempts"));
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":wait"));
                Assert.Equal(5, CountAllKeys());
                await Task.Delay(1000);

                await queue.DoMaintenanceWorkAsync();
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id));
                Assert.Equal(1, await db.ListLengthAsync("q:SimpleWorkItem:in"));
                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:work"));
                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:wait"));
                Assert.False(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":dequeued"));
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":enqueued"));
                Assert.False(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":renewed"));
                Assert.Equal(1, await db.StringGetAsync("q:SimpleWorkItem:" + id + ":attempts"));
                Assert.False(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":wait"));
                Assert.InRange(CountAllKeys(), 4, 5);

                workItem = await queue.DequeueAsync();
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id));
                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:in"));
                Assert.Equal(1, await db.ListLengthAsync("q:SimpleWorkItem:work"));
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":dequeued"));
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":enqueued"));
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":renewed"));
                Assert.Equal(1, await db.StringGetAsync("q:SimpleWorkItem:" + id + ":attempts"));
                Assert.InRange(CountAllKeys(), 6, 7);

                await workItem.CompleteAsync();
                Assert.False(await db.KeyExistsAsync("q:SimpleWorkItem:" + id));
                Assert.False(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":enqueued"));
                Assert.False(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":dequeued"));
                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:in"));
                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:work"));
                Assert.InRange(CountAllKeys(), 0, 1);
            }
        }

        [Fact]
        public async Task CanTrimDeadletterItems() {
            var queue = GetQueue(retries: 0, workItemTimeout: TimeSpan.FromMilliseconds(50), deadLetterMaxItems: 3, runQueueMaintenance: false) as RedisQueue<SimpleWorkItem>;
            if (queue == null)
                return;
            
            using (queue) {
                var db = SharedConnection.GetMuxer().GetDatabase();
                var workItemIds = new List<string>();

                for (int i = 0; i < 10; i++) {
                    var id = await queue.EnqueueAsync(new SimpleWorkItem {Data = "blah", Id = i});
                    Trace.WriteLine(id);
                    workItemIds.Add(id);
                }

                for (int i = 0; i < 10; i++) {
                    var workItem = await queue.DequeueAsync();
                    await workItem.AbandonAsync();
                    Trace.WriteLine("Abandoning: " + workItem.Id);
                }

                workItemIds.Reverse();
                await queue.DoMaintenanceWorkAsync();

                foreach (var id in workItemIds.Take(3)) {
                    Trace.WriteLine("Checking: " + id);
                    Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id));
                }

                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:in"));
                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:work"));
                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:wait"));
                Assert.Equal(3, await db.ListLengthAsync("q:SimpleWorkItem:dead"));
                Assert.InRange(CountAllKeys(), 10, 11);
            }
        }
        
        // TODO: Need to write tests that verify the cache data is correct after each operation.

        [Fact(Skip = "Performance Test")]
        public async Task MeasureThroughputWithRandomFailures() {
            var queue = GetQueue(retries: 3, workItemTimeout: TimeSpan.FromSeconds(2), retryDelay: TimeSpan.Zero);
            if (queue == null)
                return;
            
            using (queue) {
                await queue.DeleteQueueAsync();

                const int workItemCount = 1000;
                for (int i = 0; i < workItemCount; i++) {
                    await queue.EnqueueAsync(new SimpleWorkItem {
                        Data = "Hello"
                    });
                }
                Assert.Equal(workItemCount, (await queue.GetQueueStatsAsync()).Queued);

                var metrics = new InMemoryMetricsClient();
                var workItem = await queue.DequeueAsync(TimeSpan.Zero);
                while (workItem != null) {
                    Assert.Equal("Hello", workItem.Value.Data);
                    if (RandomData.GetBool(10))
                        await workItem.AbandonAsync();
                    else
                        await workItem.CompleteAsync();

                    await metrics.CounterAsync("work");

                    workItem = await queue.DequeueAsync(TimeSpan.FromMilliseconds(100));
                }
                _logger.Trace((await metrics.GetCounterStatsAsync("work")).ToString());

                var stats = await queue.GetQueueStatsAsync();
                Assert.True(stats.Dequeued >= workItemCount);
                Assert.Equal(workItemCount, stats.Completed + stats.Deadletter);
                Assert.Equal(0, stats.Queued);

                Trace.WriteLine(CountAllKeys());
            }
        }

        [Fact(Skip = "Performance Test")]
        public async Task MeasureThroughput() {
            var queue = GetQueue(retries: 3, workItemTimeout: TimeSpan.FromSeconds(2), retryDelay: TimeSpan.FromSeconds(1));
            if (queue == null)
                return;
            
            using (queue) {
                await queue.DeleteQueueAsync();

                const int workItemCount = 1000;
                for (int i = 0; i < workItemCount; i++) {
                    await queue.EnqueueAsync(new SimpleWorkItem {
                        Data = "Hello"
                    });
                }
                Assert.Equal(workItemCount, (await queue.GetQueueStatsAsync()).Queued);

                var metrics = new InMemoryMetricsClient();
                var workItem = await queue.DequeueAsync(TimeSpan.Zero);
                while (workItem != null) {
                    Assert.Equal("Hello", workItem.Value.Data);
                    await workItem.CompleteAsync();
                    await metrics.CounterAsync("work");

                    workItem = await queue.DequeueAsync(TimeSpan.Zero);
                }
                _logger.Trace((await metrics.GetCounterStatsAsync("work")).ToString());

                var stats = await queue.GetQueueStatsAsync();
                Assert.Equal(workItemCount, stats.Dequeued);
                Assert.Equal(workItemCount, stats.Completed);
                Assert.Equal(0, stats.Queued);

                Trace.WriteLine(CountAllKeys());
            }
        }

        [Fact(Skip = "Performance Test")]
        public async Task MeasureWorkerThroughput() {
            var queue = GetQueue(retries: 3, workItemTimeout: TimeSpan.FromSeconds(2), retryDelay: TimeSpan.FromSeconds(1));
            if (queue == null)
                return;
            
            using (queue) {
                await queue.DeleteQueueAsync();

                const int workItemCount = 1;
                for (int i = 0; i < workItemCount; i++) {
                    await queue.EnqueueAsync(new SimpleWorkItem {
                        Data = "Hello"
                    });
                }
                Assert.Equal(workItemCount, (await queue.GetQueueStatsAsync()).Queued);

                var countdown = new AsyncCountdownEvent(workItemCount);
                var metrics = new InMemoryMetricsClient();
                await queue.StartWorkingAsync(async workItem => {
                    Assert.Equal("Hello", workItem.Value.Data);
                    await workItem.CompleteAsync();
                    await metrics.CounterAsync("work");
                    countdown.Signal();
                });

                await countdown.WaitAsync(TimeSpan.FromMinutes(1));
                _logger.Trace((await metrics.GetCounterStatsAsync("work")).ToString());

                var stats = await queue.GetQueueStatsAsync();
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
                } catch (Exception ex) {
                    _logger.Error(ex, "Error flushing redis");
                }
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
                    for (int index = 0; index < keys.Length; index++)
                        _logger.Info($"Server: {server.EndPoint} Key {index}: {keys[index]}");

                    count += keys.Length;
                } catch (Exception ex) {
                    _logger.Error(ex, "Error getting redis key count");
                }
            }

            return count;
        }
    }
}
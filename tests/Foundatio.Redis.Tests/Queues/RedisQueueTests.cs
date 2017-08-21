using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Exceptionless;
using Foundatio.Caching;
using Foundatio.Lock;
using Foundatio.Tests.Extensions;
using Foundatio.Logging;
using Foundatio.Messaging;
using Foundatio.Metrics;
using Foundatio.Queues;
using Foundatio.Redis.Tests.Extensions;
using Foundatio.Tests.Queue;
using Foundatio.Utility;
using Foundatio.AsyncEx;
using Xunit;
using Xunit.Abstractions;
#pragma warning disable 4014

namespace Foundatio.Redis.Tests.Queues {
    public class RedisQueueTests : QueueTestBase {
        public RedisQueueTests(ITestOutputHelper output) : base(output) {
            var muxer = SharedConnection.GetMuxer();
            while (muxer.CountAllKeysAsync().GetAwaiter().GetResult() != 0)
                muxer.FlushAllAsync().GetAwaiter().GetResult();
        }

        protected override IQueue<SimpleWorkItem> GetQueue(int retries = 1, TimeSpan? workItemTimeout = null, TimeSpan? retryDelay = null, int deadLetterMaxItems = 100, bool runQueueMaintenance = true) {
            var queue = new RedisQueue<SimpleWorkItem>(new RedisQueueOptions<SimpleWorkItem> {
                ConnectionMultiplexer = SharedConnection.GetMuxer(),
                Retries = retries,
                RetryDelay = retryDelay.GetValueOrDefault(TimeSpan.FromMinutes(1)),
                DeadLetterMaxItems = deadLetterMaxItems,
                WorkItemTimeout = workItemTimeout.GetValueOrDefault(TimeSpan.FromMinutes(5)),
                RunMaintenanceTasks = runQueueMaintenance,
                LoggerFactory = Log
            });

            _logger.Debug("Queue Id: {queueId}", queue.QueueId);
            return queue;
        }

        [Fact]
        public override Task CanQueueAndDequeueWorkItemAsync() {
            return base.CanQueueAndDequeueWorkItemAsync();
        }

        [Fact]
        public override Task CanDequeueWithCancelledTokenAsync() {
            return base.CanDequeueWithCancelledTokenAsync();
        }

        [Fact]
        public override Task CanDequeueEfficientlyAsync() {
            return base.CanDequeueEfficientlyAsync();
        }

        [Fact]
        public override Task CanResumeDequeueEfficientlyAsync() {
            return base.CanResumeDequeueEfficientlyAsync();
        }

        [Fact]
        public override Task CanQueueAndDequeueMultipleWorkItemsAsync() {
            return base.CanQueueAndDequeueMultipleWorkItemsAsync();
        }

        [Fact]
        public override Task WillNotWaitForItemAsync() {
            return base.WillNotWaitForItemAsync();
        }

        [Fact]
        public override Task WillWaitForItemAsync() {
            return base.WillWaitForItemAsync();
        }

        [Fact]
        public override Task DequeueWaitWillGetSignaledAsync() {
            return base.DequeueWaitWillGetSignaledAsync();
        }

        [Fact]
        public override Task CanUseQueueWorkerAsync() {
            return base.CanUseQueueWorkerAsync();
        }

        [Fact]
        public override Task CanRenewLockAsync() {
            return base.CanRenewLockAsync();
        }

        [Fact]
        public override Task CanHandleErrorInWorkerAsync() {
            return base.CanHandleErrorInWorkerAsync();
        }

        [Fact]
        public override Task WorkItemsWillTimeoutAsync() {
            return base.WorkItemsWillTimeoutAsync();
        }

        [Fact]
        public override Task WorkItemsWillGetMovedToDeadletterAsync() {
            return base.WorkItemsWillGetMovedToDeadletterAsync();
        }

        [Fact]
        public override Task CanAutoCompleteWorkerAsync() {
            return base.CanAutoCompleteWorkerAsync();
        }

        [Fact]
        public override Task CanHaveMultipleQueueInstancesAsync() {
            return base.CanHaveMultipleQueueInstancesAsync();
        }

        [Fact]
        public override Task CanDelayRetryAsync() {
            return base.CanDelayRetryAsync();
        }

        [Fact]
        public override Task CanRunWorkItemWithMetricsAsync() {
            return base.CanRunWorkItemWithMetricsAsync();
        }

        [Fact]
        public override Task CanAbandonQueueEntryOnceAsync() {
            return base.CanAbandonQueueEntryOnceAsync();
        }

        [Fact]
        public override Task CanCompleteQueueEntryOnceAsync() {
            return base.CanCompleteQueueEntryOnceAsync();
        }

        [Fact]
        public override async Task CanDequeueWithLockingAsync() {
            var muxer = SharedConnection.GetMuxer();
            using (var cache = new RedisCacheClient(new RedisCacheClientOptions { ConnectionMultiplexer = muxer, LoggerFactory = Log })) {
                using (var messageBus = new RedisMessageBus(new RedisMessageBusOptions { Subscriber = muxer.GetSubscriber(), Topic = "test-queue", LoggerFactory = Log })) {
                    var distributedLock = new CacheLockProvider(cache, messageBus, Log);
                    await CanDequeueWithLockingImpAsync(distributedLock);
                }
            }
        }

        [Fact]
        public override async Task CanHaveMultipleQueueInstancesWithLockingAsync() {
            var muxer = SharedConnection.GetMuxer();
            using (var cache = new RedisCacheClient(new RedisCacheClientOptions { ConnectionMultiplexer = muxer, LoggerFactory = Log })) {
                using (var messageBus = new RedisMessageBus(new RedisMessageBusOptions { Subscriber = muxer.GetSubscriber(), Topic = "test-queue", LoggerFactory = Log })) {
                    var distributedLock = new CacheLockProvider(cache, messageBus, Log);
                    await CanHaveMultipleQueueInstancesWithLockingImplAsync(distributedLock);
                }
            }
        }

        [Fact]
        public async Task VerifyCacheKeysAreCorrect() {
            var queue = GetQueue(retries: 3, workItemTimeout: TimeSpan.FromSeconds(2), retryDelay: TimeSpan.Zero, runQueueMaintenance: false);
            if (queue == null)
                return;

            using (queue) {
                var muxer = SharedConnection.GetMuxer();
                var db = muxer.GetDatabase();

                string id = await queue.EnqueueAsync(new SimpleWorkItem { Data = "blah", Id = 1 });
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id));
                Assert.Equal(1, await db.ListLengthAsync("q:SimpleWorkItem:in"));
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":enqueued"));
                Assert.Equal(3, await muxer.CountAllKeysAsync());

                _logger.Info("-----");

                var workItem = await queue.DequeueAsync();
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id));
                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:in"));
                Assert.Equal(1, await db.ListLengthAsync("q:SimpleWorkItem:work"));
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":enqueued"));
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":renewed"));
                Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":dequeued"));
                Assert.Equal(5, await muxer.CountAllKeysAsync());

                _logger.Info("-----");

                await workItem.CompleteAsync();
                Assert.False(await db.KeyExistsAsync("q:SimpleWorkItem:" + id));
                Assert.False(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":enqueued"));
                Assert.False(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":renewed"));
                Assert.False(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":dequeued"));
                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:in"));
                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:work"));
                Assert.Equal(0, await muxer.CountAllKeysAsync());
            }
        }

        [Fact]
        public async Task VerifyCacheKeysAreCorrectAfterAbandon() {
            var queue = GetQueue(retries: 2, workItemTimeout: TimeSpan.FromMilliseconds(100), retryDelay: TimeSpan.Zero, runQueueMaintenance: false) as RedisQueue<SimpleWorkItem>;
            if (queue == null)
                return;

            using (TestSystemClock.Install()) {
                using (queue) {
                    var muxer = SharedConnection.GetMuxer();
                    var db = muxer.GetDatabase();

                    string id = await queue.EnqueueAsync(new SimpleWorkItem {
                        Data = "blah",
                        Id = 1
                    });
                    _logger.Trace("SimpleWorkItem Id: {0}", id);

                    var workItem = await queue.DequeueAsync();
                    await workItem.AbandonAsync();
                    Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id));
                    Assert.Equal(1, await db.ListLengthAsync("q:SimpleWorkItem:in"));
                    Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:work"));
                    Assert.False(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":dequeued"));
                    Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":enqueued"));
                    Assert.False(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":renewed"));
                    Assert.Equal(1, await db.StringGetAsync("q:SimpleWorkItem:" + id + ":attempts"));
                    Assert.Equal(4, await muxer.CountAllKeysAsync());

                    workItem = await queue.DequeueAsync();
                    Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id));
                    Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:in"));
                    Assert.Equal(1, await db.ListLengthAsync("q:SimpleWorkItem:work"));
                    Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":dequeued"));
                    Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":enqueued"));
                    Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":renewed"));
                    Assert.Equal(1, await db.StringGetAsync("q:SimpleWorkItem:" + id + ":attempts"));
                    Assert.Equal(6, await muxer.CountAllKeysAsync());

                    // let the work item timeout and become auto abandoned.
                    SystemClock.Test.AddTime(TimeSpan.FromMilliseconds(250));
                    await queue.DoMaintenanceWorkAsync();
                    Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id));
                    Assert.Equal(1, await db.ListLengthAsync("q:SimpleWorkItem:in"));
                    Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:work"));
                    Assert.False(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":dequeued"));
                    Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":enqueued"));
                    Assert.False(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":renewed"));
                    Assert.Equal(2, await db.StringGetAsync("q:SimpleWorkItem:" + id + ":attempts"));
                    Assert.Equal(1, (await queue.GetQueueStatsAsync()).Timeouts);
                    Assert.InRange(await muxer.CountAllKeysAsync(), 3, 4);

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
                    Assert.InRange(await muxer.CountAllKeysAsync(), 4, 5);
                }
            }
        }

        [Fact]
        public async Task VerifyCacheKeysAreCorrectAfterAbandonWithRetryDelay() {
            var queue = GetQueue(retries: 2, workItemTimeout: TimeSpan.FromMilliseconds(100), retryDelay: TimeSpan.FromMilliseconds(250), runQueueMaintenance: false) as RedisQueue<SimpleWorkItem>;
            if (queue == null)
                return;

            using (TestSystemClock.Install()) {
                using (queue) {
                    var muxer = SharedConnection.GetMuxer();
                    var db = muxer.GetDatabase();

                    string id = await queue.EnqueueAsync(new SimpleWorkItem {
                        Data = "blah",
                        Id = 1
                    });
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
                    Assert.Equal(5, await muxer.CountAllKeysAsync());

                    SystemClock.Test.AddTime(TimeSpan.FromSeconds(1));
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
                    Assert.InRange(await muxer.CountAllKeysAsync(), 4, 5);

                    workItem = await queue.DequeueAsync();
                    Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id));
                    Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:in"));
                    Assert.Equal(1, await db.ListLengthAsync("q:SimpleWorkItem:work"));
                    Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":dequeued"));
                    Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":enqueued"));
                    Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":renewed"));
                    Assert.Equal(1, await db.StringGetAsync("q:SimpleWorkItem:" + id + ":attempts"));
                    Assert.InRange(await muxer.CountAllKeysAsync(), 6, 7);

                    await workItem.CompleteAsync();
                    Assert.False(await db.KeyExistsAsync("q:SimpleWorkItem:" + id));
                    Assert.False(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":enqueued"));
                    Assert.False(await db.KeyExistsAsync("q:SimpleWorkItem:" + id + ":dequeued"));
                    Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:in"));
                    Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:work"));
                    Assert.InRange(await muxer.CountAllKeysAsync(), 0, 1);
                }
            }
        }

        [Fact]
        public async Task CanTrimDeadletterItems() {
            var queue = GetQueue(retries: 0, workItemTimeout: TimeSpan.FromMilliseconds(50), deadLetterMaxItems: 3, runQueueMaintenance: false) as RedisQueue<SimpleWorkItem>;
            if (queue == null)
                return;

            using (queue) {
                var muxer = SharedConnection.GetMuxer();
                var db = muxer.GetDatabase();

                var workItemIds = new List<string>();
                for (int i = 0; i < 10; i++) {
                    string id = await queue.EnqueueAsync(new SimpleWorkItem {Data = "blah", Id = i});
                    _logger.Trace(id);
                    workItemIds.Add(id);
                }

                for (int i = 0; i < 10; i++) {
                    var workItem = await queue.DequeueAsync();
                    await workItem.AbandonAsync();
                    _logger.Trace("Abandoning: " + workItem.Id);
                }

                workItemIds.Reverse();
                await queue.DoMaintenanceWorkAsync();

                foreach (object id in workItemIds.Take(3)) {
                    _logger.Trace("Checking: " + id);
                    Assert.True(await db.KeyExistsAsync("q:SimpleWorkItem:" + id));
                }

                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:in"));
                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:work"));
                Assert.Equal(0, await db.ListLengthAsync("q:SimpleWorkItem:wait"));
                Assert.Equal(3, await db.ListLengthAsync("q:SimpleWorkItem:dead"));
                Assert.InRange(await muxer.CountAllKeysAsync(), 10, 11);
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

                var metrics = new InMemoryMetricsClient(new InMemoryMetricsClientOptions());
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

                var muxer = SharedConnection.GetMuxer();
                _logger.Trace("# Keys: {0}", muxer.CountAllKeysAsync());
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

                var metrics = new InMemoryMetricsClient(new InMemoryMetricsClientOptions());
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

                var muxer = SharedConnection.GetMuxer();
                _logger.Trace("# Keys: {0}", muxer.CountAllKeysAsync());
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
                var metrics = new InMemoryMetricsClient(new InMemoryMetricsClientOptions());
                await queue.StartWorkingAsync(async workItem => {
                    Assert.Equal("Hello", workItem.Value.Data);
                    await workItem.CompleteAsync();
                    await metrics.CounterAsync("work");
                    countdown.Signal();
                });

                await countdown.WaitAsync(TimeSpan.FromMinutes(1));
                Assert.Equal(0, countdown.CurrentCount);
                _logger.Trace((await metrics.GetCounterStatsAsync("work")).ToString());

                var stats = await queue.GetQueueStatsAsync();
                Assert.Equal(workItemCount, stats.Dequeued);
                Assert.Equal(workItemCount, stats.Completed);
                Assert.Equal(0, stats.Queued);

                var muxer = SharedConnection.GetMuxer();
                _logger.Trace("# Keys: {0}", muxer.CountAllKeysAsync());
            }
        }
    }
}
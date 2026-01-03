using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Exceptionless;
using Foundatio.AsyncEx;
using Foundatio.Caching;
using Foundatio.Lock;
using Foundatio.Messaging;
using Foundatio.Queues;
using Foundatio.Redis.Tests.Extensions;
using Foundatio.Tests.Extensions;
using Foundatio.Tests.Queue;
using Foundatio.Tests.Utility;
using Foundatio.Xunit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;
using Xunit;

#pragma warning disable 4014

namespace Foundatio.Redis.Tests.Queues;

public class RedisQueueTests : QueueTestBase, IAsyncLifetime
{
    public RedisQueueTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override IQueue<SimpleWorkItem> GetQueue(int retries = 1, TimeSpan? workItemTimeout = null, TimeSpan? retryDelay = null, int[] retryMultipliers = null, int deadLetterMaxItems = 100, bool runQueueMaintenance = true, TimeProvider timeProvider = null)
    {
        var queue = new RedisQueue<SimpleWorkItem>(o => o
            .ConnectionMultiplexer(SharedConnection.GetMuxer(Log))
            .Retries(retries)
            .RetryDelay(retryDelay.GetValueOrDefault(TimeSpan.FromMinutes(1)))
            .RetryMultipliers(retryMultipliers ?? [1, 3, 5, 10])
            .DeadLetterMaxItems(deadLetterMaxItems)
            .TimeProvider(timeProvider ?? TimeProvider.System)
            .WorkItemTimeout(workItemTimeout.GetValueOrDefault(TimeSpan.FromMinutes(5)))
            .MetricsPollingInterval(TimeSpan.Zero)
            .RunMaintenanceTasks(runQueueMaintenance)
            .LoggerFactory(Log)
        );

        _logger.LogDebug("Queue Id: {QueueId}", queue.QueueId);
        return queue;
    }

    [Fact]
    public override Task CanQueueAndDequeueWorkItemAsync()
    {
        return base.CanQueueAndDequeueWorkItemAsync();
    }

    [Fact(Skip = "DeliveryDelay is not supported")]
    public override Task CanQueueAndDequeueWorkItemWithDelayAsync()
    {
        return base.CanQueueAndDequeueWorkItemWithDelayAsync();
    }

    [Fact]
    public override Task CanUseQueueOptionsAsync()
    {
        return base.CanUseQueueOptionsAsync();
    }

    [Fact]
    public override Task CanDiscardDuplicateQueueEntriesAsync()
    {
        return base.CanDiscardDuplicateQueueEntriesAsync();
    }

    [Fact]
    public override Task CanDequeueWithCancelledTokenAsync()
    {
        return base.CanDequeueWithCancelledTokenAsync();
    }

    [Fact]
    public override Task CanDequeueEfficientlyAsync()
    {
        return base.CanDequeueEfficientlyAsync();
    }

    [Fact]
    public override Task CanResumeDequeueEfficientlyAsync()
    {
        return base.CanResumeDequeueEfficientlyAsync();
    }

    [Fact]
    public override Task CanQueueAndDequeueMultipleWorkItemsAsync()
    {
        return base.CanQueueAndDequeueMultipleWorkItemsAsync();
    }

    [Fact]
    public override Task WillNotWaitForItemAsync()
    {
        return base.WillNotWaitForItemAsync();
    }

    [Fact]
    public override Task WillWaitForItemAsync()
    {
        return base.WillWaitForItemAsync();
    }

    [Fact]
    public override Task DequeueWaitWillGetSignaledAsync()
    {
        return base.DequeueWaitWillGetSignaledAsync();
    }

    [Fact]
    public override Task CanUseQueueWorkerAsync()
    {
        return base.CanUseQueueWorkerAsync();
    }

    [Fact]
    public override Task CanHandleErrorInWorkerAsync()
    {
        return base.CanHandleErrorInWorkerAsync();
    }

    [Fact]
    public override Task WorkItemsWillTimeoutAsync()
    {
        return base.WorkItemsWillTimeoutAsync();
    }

    [Fact]
    public override Task WorkItemsWillGetMovedToDeadletterAsync()
    {
        return base.WorkItemsWillGetMovedToDeadletterAsync();
    }

    [Fact]
    public override Task CanAutoCompleteWorkerAsync()
    {
        return base.CanAutoCompleteWorkerAsync();
    }

    [Fact]
    public override Task CanHaveMultipleQueueInstancesAsync()
    {
        return base.CanHaveMultipleQueueInstancesAsync();
    }

    [Fact]
    public override Task CanDelayRetryAsync()
    {
        return base.CanDelayRetryAsync();
    }

    [Fact]
    public override Task CanRunWorkItemWithMetricsAsync()
    {
        return base.CanRunWorkItemWithMetricsAsync();
    }

    [Fact]
    public override Task CanRenewLockAsync()
    {
        return base.CanRenewLockAsync();
    }

    [Fact]
    public override Task CanAbandonQueueEntryOnceAsync()
    {
        return base.CanAbandonQueueEntryOnceAsync();
    }

    [Fact]
    public override Task CanCompleteQueueEntryOnceAsync()
    {
        return base.CanCompleteQueueEntryOnceAsync();
    }

    [RetryFact]
    public override async Task CanDequeueWithLockingAsync()
    {
        var muxer = SharedConnection.GetMuxer(Log);
        using var cache = new RedisCacheClient(new RedisCacheClientOptions { ConnectionMultiplexer = muxer, LoggerFactory = Log });
        using var messageBus = new RedisMessageBus(new RedisMessageBusOptions { Subscriber = muxer.GetSubscriber(), Topic = "test-queue", LoggerFactory = Log });
        var distributedLock = new CacheLockProvider(cache, messageBus, null, Log);
        await CanDequeueWithLockingImpAsync(distributedLock);
    }

    [Fact]
    public override async Task CanHaveMultipleQueueInstancesWithLockingAsync()
    {
        var muxer = SharedConnection.GetMuxer(Log);
        using var cache = new RedisCacheClient(new RedisCacheClientOptions { ConnectionMultiplexer = muxer, LoggerFactory = Log });
        using var messageBus = new RedisMessageBus(new RedisMessageBusOptions { Subscriber = muxer.GetSubscriber(), Topic = "test-queue", LoggerFactory = Log });
        var distributedLock = new CacheLockProvider(cache, messageBus, null, Log);
        await CanHaveMultipleQueueInstancesWithLockingImplAsync(distributedLock);
    }

    [Fact]
    public override Task MaintainJobNotAbandon_NotWorkTimeOutEntry()
    {
        return base.MaintainJobNotAbandon_NotWorkTimeOutEntry();
    }

    [Fact]
    public override Task VerifyRetryAttemptsAsync()
    {
        return base.VerifyRetryAttemptsAsync();
    }

    [Fact]
    public override Task VerifyDelayedRetryAttemptsAsync()
    {
        return base.VerifyDelayedRetryAttemptsAsync();
    }

    [Fact]
    public override Task CanHandleAutoAbandonInWorker()
    {
        return base.CanHandleAutoAbandonInWorker();
    }

    [Fact]
    public async Task VerifyCacheKeysAreCorrect()
    {
        var queue = GetQueue(retries: 3, workItemTimeout: TimeSpan.FromSeconds(2), retryDelay: TimeSpan.Zero, runQueueMaintenance: false);
        if (queue == null)
            return;

        using (queue)
        {
            var muxer = SharedConnection.GetMuxer(Log);
            var db = muxer.GetDatabase();
            string listPrefix = muxer.IsCluster() ? "{q:SimpleWorkItem}" : "q:SimpleWorkItem";

            string id = await queue.EnqueueAsync(new SimpleWorkItem { Data = "blah", Id = 1 });
            Assert.True(await db.KeyExistsAsync($"{listPrefix}:{id}"));
            Assert.Equal(1, await db.ListLengthAsync($"{listPrefix}:in"));
            Assert.True(await db.KeyExistsAsync($"{listPrefix}:{id}:enqueued"));
            Assert.Equal(3, await muxer.CountAllKeysAsync());

            _logger.LogInformation("-----");

            Assert.False(await db.KeyExistsAsync($"{listPrefix}:{id}:renewed"));
            var workItem = await queue.DequeueAsync();
            Assert.True(await db.KeyExistsAsync($"{listPrefix}:{id}"));
            Assert.Equal(0, await db.ListLengthAsync($"{listPrefix}:in"));
            Assert.Equal(1, await db.ListLengthAsync($"{listPrefix}:work"));
            Assert.True(await db.KeyExistsAsync($"{listPrefix}:{id}:enqueued"));
            Assert.True(await db.KeyExistsAsync($"{listPrefix}:{id}:renewed"));
            Assert.True(await db.KeyExistsAsync($"{listPrefix}:{id}:dequeued"));
            Assert.Equal(5, await muxer.CountAllKeysAsync());

            await Task.Delay(TimeSpan.FromSeconds(4), TestCancellationToken);

            Assert.True(await db.KeyExistsAsync($"{listPrefix}:{id}"));
            Assert.Equal(0, await db.ListLengthAsync($"{listPrefix}:in"));
            Assert.Equal(1, await db.ListLengthAsync($"{listPrefix}:work"));
            Assert.True(await db.KeyExistsAsync($"{listPrefix}:{id}:enqueued"));
            Assert.True(await db.KeyExistsAsync($"{listPrefix}:{id}:renewed"));
            Assert.True(await db.KeyExistsAsync($"{listPrefix}:{id}:dequeued"));
            Assert.Equal(5, await muxer.CountAllKeysAsync());

            _logger.LogInformation("-----");

            await workItem.CompleteAsync();
            Assert.False(await db.KeyExistsAsync($"{listPrefix}:{id}"));
            Assert.False(await db.KeyExistsAsync($"{listPrefix}:{id}:enqueued"));
            Assert.False(await db.KeyExistsAsync($"{listPrefix}:{id}:renewed"));
            Assert.False(await db.KeyExistsAsync($"{listPrefix}:{id}:dequeued"));
            Assert.Equal(0, await db.ListLengthAsync($"{listPrefix}:in"));
            Assert.Equal(0, await db.ListLengthAsync($"{listPrefix}:work"));
            Assert.Equal(0, await muxer.CountAllKeysAsync());
        }
    }

    [Fact]
    public async Task VerifyCacheKeysAreCorrectAfterAbandon()
    {
        var timeProvider = new FakeTimeProvider();
        var queue = GetQueue(retries: 2, workItemTimeout: TimeSpan.FromMilliseconds(100), retryDelay: TimeSpan.Zero, runQueueMaintenance: false, timeProvider: timeProvider) as RedisQueue<SimpleWorkItem>;
        if (queue == null)
            return;

        using RedisQueue<SimpleWorkItem> redisQueue = queue;
        var muxer = SharedConnection.GetMuxer(Log);
        var db = muxer.GetDatabase();
        string listPrefix = muxer.IsCluster() ? "{q:SimpleWorkItem}" : "q:SimpleWorkItem";

        string id = await queue.EnqueueAsync(new SimpleWorkItem
        {
            Data = "blah",
            Id = 1
        });
        _logger.LogTrace("SimpleWorkItem Id: {Id}", id);

        var workItem = await queue.DequeueAsync();
        await workItem.AbandonAsync();
        Assert.True(await db.KeyExistsAsync($"{listPrefix}:{id}"));
        Assert.Equal(1, await db.ListLengthAsync($"{listPrefix}:in"));
        Assert.Equal(0, await db.ListLengthAsync($"{listPrefix}:work"));
        Assert.False(await db.KeyExistsAsync($"{listPrefix}:{id}:dequeued"));
        Assert.True(await db.KeyExistsAsync($"{listPrefix}:{id}:enqueued"));
        Assert.False(await db.KeyExistsAsync($"{listPrefix}:{id}:renewed"));
        Assert.Equal(1, await db.StringGetAsync($"{listPrefix}:{id}:attempts"));
        Assert.Equal(4, await muxer.CountAllKeysAsync());

        workItem = await queue.DequeueAsync();
        Assert.NotNull(workItem);
        Assert.True(await db.KeyExistsAsync($"{listPrefix}:{id}"));
        Assert.Equal(0, await db.ListLengthAsync($"{listPrefix}:in"));
        Assert.Equal(1, await db.ListLengthAsync($"{listPrefix}:work"));
        Assert.True(await db.KeyExistsAsync($"{listPrefix}:{id}:dequeued"));
        Assert.True(await db.KeyExistsAsync($"{listPrefix}:{id}:enqueued"));
        Assert.True(await db.KeyExistsAsync($"{listPrefix}:{id}:renewed"));
        Assert.Equal(1, await db.StringGetAsync($"{listPrefix}:{id}:attempts"));
        Assert.Equal(6, await muxer.CountAllKeysAsync());

        // let the work item timeout and become auto abandoned.
        timeProvider.Advance(TimeSpan.FromMilliseconds(250));
        await queue.DoMaintenanceWorkAsync();
        Assert.True(await db.KeyExistsAsync($"{listPrefix}:{id}"));
        Assert.Equal(1, await db.ListLengthAsync($"{listPrefix}:in"));
        Assert.Equal(0, await db.ListLengthAsync($"{listPrefix}:work"));
        Assert.False(await db.KeyExistsAsync($"{listPrefix}:{id}:dequeued"));
        Assert.True(await db.KeyExistsAsync($"{listPrefix}:{id}:enqueued"));
        Assert.False(await db.KeyExistsAsync($"{listPrefix}:{id}:renewed"));
        Assert.Equal(2, await db.StringGetAsync($"{listPrefix}:{id}:attempts"));
        Assert.Equal(1, (await queue.GetQueueStatsAsync()).Timeouts);
        Assert.InRange(await muxer.CountAllKeysAsync(), 3, 4);

        // should go to deadletter now
        workItem = await queue.DequeueAsync();
        await workItem.AbandonAsync();
        Assert.True(await db.KeyExistsAsync($"{listPrefix}:{id}"));
        Assert.Equal(0, await db.ListLengthAsync($"{listPrefix}:in"));
        Assert.Equal(0, await db.ListLengthAsync($"{listPrefix}:work"));
        Assert.Equal(1, await db.ListLengthAsync($"{listPrefix}:dead"));
        Assert.False(await db.KeyExistsAsync($"{listPrefix}:{id}:dequeued"));
        Assert.True(await db.KeyExistsAsync($"{listPrefix}:{id}:enqueued"));
        Assert.False(await db.KeyExistsAsync($"{listPrefix}:{id}:renewed"));
        Assert.Equal(3, await db.StringGetAsync($"{listPrefix}:{id}:attempts"));
        Assert.InRange(await muxer.CountAllKeysAsync(), 4, 5);
    }

    [Fact]
    public async Task VerifyCacheKeysAreCorrectAfterAbandonWithRetryDelay()
    {
        var timeProvider = new FakeTimeProvider();
        var queue = GetQueue(retries: 2, workItemTimeout: TimeSpan.FromMilliseconds(100), retryDelay: TimeSpan.FromMilliseconds(250), runQueueMaintenance: false, timeProvider: timeProvider) as RedisQueue<SimpleWorkItem>;
        if (queue == null)
            return;

        using RedisQueue<SimpleWorkItem> redisQueue = queue;
        var muxer = SharedConnection.GetMuxer(Log);
        var db = muxer.GetDatabase();
        string listPrefix = muxer.IsCluster() ? "{q:SimpleWorkItem}" : "q:SimpleWorkItem";

        string id = await queue.EnqueueAsync(new SimpleWorkItem
        {
            Data = "blah",
            Id = 1
        });
        var workItem = await queue.DequeueAsync();
        await workItem.AbandonAsync();
        Assert.True(await db.KeyExistsAsync($"{listPrefix}:{id}"));
        Assert.Equal(0, await db.ListLengthAsync($"{listPrefix}:in"));
        Assert.Equal(0, await db.ListLengthAsync($"{listPrefix}:work"));
        Assert.Equal(1, await db.ListLengthAsync($"{listPrefix}:wait"));
        Assert.False(await db.KeyExistsAsync($"{listPrefix}:{id}:dequeued"));
        Assert.True(await db.KeyExistsAsync($"{listPrefix}:{id}:enqueued"));
        Assert.False(await db.KeyExistsAsync($"{listPrefix}:{id}:renewed"));
        Assert.Equal(1, await db.StringGetAsync($"{listPrefix}:{id}:attempts"));
        Assert.True(await db.KeyExistsAsync($"{listPrefix}:{id}:wait"));
        Assert.Equal(5, await muxer.CountAllKeysAsync());

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await queue.DoMaintenanceWorkAsync();
        Assert.True(await db.KeyExistsAsync($"{listPrefix}:{id}"));
        Assert.Equal(1, await db.ListLengthAsync($"{listPrefix}:in"));
        Assert.Equal(0, await db.ListLengthAsync($"{listPrefix}:work"));
        Assert.Equal(0, await db.ListLengthAsync($"{listPrefix}:wait"));
        Assert.False(await db.KeyExistsAsync($"{listPrefix}:{id}:dequeued"));
        Assert.True(await db.KeyExistsAsync($"{listPrefix}:{id}:enqueued"));
        Assert.False(await db.KeyExistsAsync($"{listPrefix}:{id}:renewed"));
        Assert.Equal(1, await db.StringGetAsync($"{listPrefix}:{id}:attempts"));
        Assert.False(await db.KeyExistsAsync($"{listPrefix}:{id}:wait"));
        Assert.InRange(await muxer.CountAllKeysAsync(), 4, 5);

        workItem = await queue.DequeueAsync();
        Assert.True(await db.KeyExistsAsync($"{listPrefix}:{id}"));
        Assert.Equal(0, await db.ListLengthAsync($"{listPrefix}:in"));
        Assert.Equal(1, await db.ListLengthAsync($"{listPrefix}:work"));
        Assert.True(await db.KeyExistsAsync($"{listPrefix}:{id}:dequeued"));
        Assert.True(await db.KeyExistsAsync($"{listPrefix}:{id}:enqueued"));
        Assert.True(await db.KeyExistsAsync($"{listPrefix}:{id}:renewed"));
        Assert.Equal(1, await db.StringGetAsync($"{listPrefix}:{id}:attempts"));
        Assert.InRange(await muxer.CountAllKeysAsync(), 6, 7);

        await workItem.CompleteAsync();
        Assert.False(await db.KeyExistsAsync($"{listPrefix}:{id}"));
        Assert.False(await db.KeyExistsAsync($"{listPrefix}:{id}:enqueued"));
        Assert.False(await db.KeyExistsAsync($"{listPrefix}:{id}:dequeued"));
        Assert.Equal(0, await db.ListLengthAsync($"{listPrefix}:in"));
        Assert.Equal(0, await db.ListLengthAsync($"{listPrefix}:work"));
        Assert.InRange(await muxer.CountAllKeysAsync(), 0, 1);
    }

    [Fact]
    public async Task CanTrimDeadletterItems()
    {
        var queue = GetQueue(retries: 0, workItemTimeout: TimeSpan.FromMilliseconds(50), deadLetterMaxItems: 3, runQueueMaintenance: false) as RedisQueue<SimpleWorkItem>;
        if (queue == null)
            return;

        using RedisQueue<SimpleWorkItem> redisQueue = queue;
        var muxer = SharedConnection.GetMuxer(Log);
        var db = muxer.GetDatabase();
        string listPrefix = muxer.IsCluster() ? "{q:SimpleWorkItem}" : "q:SimpleWorkItem";

        var workItemIds = new List<string>();
        for (int i = 0; i < 10; i++)
        {
            string id = await queue.EnqueueAsync(new SimpleWorkItem { Data = "blah", Id = i });
            _logger.LogTrace(id);
            workItemIds.Add(id);
        }

        for (int i = 0; i < 10; i++)
        {
            var workItem = await queue.DequeueAsync();
            await workItem.AbandonAsync();
            _logger.LogTrace("Abandoning: {Id}", workItem.Id);
        }

        workItemIds.Reverse();
        await queue.DoMaintenanceWorkAsync();

        foreach (object id in workItemIds.Take(3))
        {
            _logger.LogTrace("Checking: {Id}", id);
            Assert.True(await db.KeyExistsAsync($"{listPrefix}:{id}"));
        }

        Assert.Equal(0, await db.ListLengthAsync($"{listPrefix}:in"));
        Assert.Equal(0, await db.ListLengthAsync($"{listPrefix}:work"));
        Assert.Equal(0, await db.ListLengthAsync($"{listPrefix}:wait"));
        Assert.Equal(3, await db.ListLengthAsync($"{listPrefix}:dead"));
        Assert.InRange(await muxer.CountAllKeysAsync(), 10, 11);
    }

    [Fact]
    public async Task VerifyFirstDequeueTimeout()
    {
        var workItemTimeout = TimeSpan.FromMilliseconds(100);
        string itemData = "blah";
        int itemId = 1;

        var queue = GetQueue(retries: 0, workItemTimeout: workItemTimeout, retryDelay: TimeSpan.Zero, runQueueMaintenance: false) as RedisQueue<SimpleWorkItem>;
        if (queue == null)
            return;

        using RedisQueue<SimpleWorkItem> redisQueue = queue;
        await queue.DeleteQueueAsync();

        // Start DequeueAsync but allow it to yield.
        var itemTask = queue.DequeueAsync();

        // Wait longer than the workItemTimeout.
        // This is the period between a queue having DequeueAsync called on it and the first item being enqueued.
        await Task.Delay(workItemTimeout.Add(TimeSpan.FromMilliseconds(1)), TestCancellationToken);

        // Add an item. DequeueAsync can now return.
        string id = await queue.EnqueueAsync(new SimpleWorkItem
        {
            Data = itemData,
            Id = itemId
        });

        Assert.NotNull(id);

        // Run DoMaintenanceWorkAsync to verify that our item will not be auto-abandoned.
        await queue.DoMaintenanceWorkAsync();

        // Completing the item will throw if the item is abandoned.
        var item = await itemTask;
        await item.CompleteAsync();

        var value = item.Value;
        Assert.NotNull(value);
        Assert.Equal(itemData, value.Data);
        Assert.Equal(itemId, value.Id);
    }

    // test to reproduce issue #64 - https://github.com/FoundatioFx/Foundatio.Redis/issues/64
    //[Fact(Skip ="This test needs to simulate database timeout which makes the runtime ~5 sec which might be too big to be run automatically")]
    [RetryFact]
    public async Task DatabaseTimeoutDuringDequeueHandledCorrectly()
    {
        // not using GetQueue() here because I need to change the ops timeout in the redis connection string
        const int OPS_TIMEOUT_MS = 100;
        string connectionString =
            $"{Configuration.GetConnectionString("RedisConnectionString")},syncTimeout={OPS_TIMEOUT_MS},asyncTimeout={OPS_TIMEOUT_MS}";
        var muxer = await ConnectionMultiplexer.ConnectAsync(connectionString);

        const string QUEUE_NAME = "test-timeout";
        var queue = new RedisQueue<SimpleWorkItem>(o => o
            .ConnectionMultiplexer(muxer)
            .LoggerFactory(Log)
            .Name(QUEUE_NAME)
            .RunMaintenanceTasks(false)
        );

        await queue.DeleteQueueAsync();

        // enqueue item to queue, no reader yet
        await queue.EnqueueAsync(new SimpleWorkItem());

        // to create a database timeout, we want to cause delay in redis to reproduce the issue
        var database = muxer.GetDatabase();

        // sync / async ops timeout is not working as described: https://stackexchange.github.io/StackExchange.Redis/Configuration
        // it should have timed out after 100 ms, but it actually takes a lot more time to time out so we have to use longer delay until this issue is resolved
        // value can be up to 1,000,000 - 1
        //const int DELAY_TIME_USEC = 200000; // 200 msec
        //string databaseDelayScript = $"local usecnow = tonumber(redis.call(\"time\")[2]); while ((((tonumber(redis.call(\"time\")[2]) - usecnow) + 1000000) % 1000000) < {DELAY_TIME_USEC}) do end";

        const int DELAY_TIME_SEC = 5;
        string databaseDelayScript = $@"
local now = tonumber(redis.call(""time"")[1]);
while ((((tonumber(redis.call(""time"")[1]) - now))) < {DELAY_TIME_SEC}) do end";

        // db will be busy for DELAY_TIME_SEC which will cause timeout on the dequeue to follow
        database.ScriptEvaluateAsync(databaseDelayScript);

        var completion = new TaskCompletionSource<bool>();
        await queue.StartWorkingAsync(async (item) =>
        {
            await item.CompleteAsync();
            completion.SetResult(true);
        });

        // wait for the databaseDelayScript to finish - the script blocks ALL Redis connections
        // so we must wait before trying to verify with any connection
        await Task.Delay((DELAY_TIME_SEC + 1) * 1000);

        // item should've either timed out at some iterations and after databaseDelayScript is done be received
        // or it might have moved to work, in this case we want to make sure the correct keys were created
        var stopwatch = Stopwatch.StartNew();
        bool success = false;
        while (stopwatch.Elapsed.TotalSeconds < 10)
        {
            string workListName = $"q:{QUEUE_NAME}:work";
            long workListLen = await database.ListLengthAsync(new RedisKey(workListName));
            var item = await database.ListLeftPopAsync(workListName);
            string dequeuedItemKey = $"q:{QUEUE_NAME}:{item}:dequeued";
            bool dequeuedItemKeyExists = await database.KeyExistsAsync(new RedisKey(dequeuedItemKey));
            if (workListLen == 1)
            {
                Assert.True(dequeuedItemKeyExists);
                success = true;
                break;
            }

            var completedTask = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromMilliseconds(100)));
            if (completion.Task == completedTask)
            {
                success = true;
                break;
            }
        }

        Assert.True(success);
    }

    // TODO: Need to write tests that verify the cache data is correct after each operation.
    [Fact(Skip = "Performance Test")]
    public async Task MeasureThroughputWithRandomFailures()
    {
        var queue = GetQueue(retries: 3, workItemTimeout: TimeSpan.FromSeconds(2), retryDelay: TimeSpan.Zero);
        if (queue == null)
            return;

        using IQueue<SimpleWorkItem> workQueue = queue;
        await queue.DeleteQueueAsync();

        const int workItemCount = 1000;
        for (int i = 0; i < workItemCount; i++)
        {
            await queue.EnqueueAsync(new SimpleWorkItem
            {
                Data = "Hello"
            });
        }
        Assert.Equal(workItemCount, (await queue.GetQueueStatsAsync()).Queued);

        int work = 0;
        var sw = Stopwatch.StartNew();
        var workItem = await queue.DequeueAsync(TimeSpan.Zero);
        while (workItem != null)
        {
            Assert.Equal("Hello", workItem.Value.Data);
            if (RandomData.GetBool(10))
                await workItem.AbandonAsync();
            else
                await workItem.CompleteAsync();

            work++;
            workItem = await queue.DequeueAsync(TimeSpan.FromMilliseconds(100));
        }
        sw.Stop();
        _logger.LogTrace("Work Items: {Count} Time: {Elapsed:g}", work, sw.Elapsed);

        var stats = await queue.GetQueueStatsAsync();
        Assert.True(stats.Dequeued >= workItemCount);
        Assert.Equal(workItemCount, stats.Completed + stats.Deadletter);
        Assert.Equal(0, stats.Queued);

        var muxer = SharedConnection.GetMuxer(Log);
        _logger.LogTrace("# Keys: {KeyCount}", await muxer.CountAllKeysAsync());
    }

    [Fact(Skip = "Performance Test")]
    public async Task MeasureThroughput()
    {
        var queue = GetQueue(retries: 3, workItemTimeout: TimeSpan.FromSeconds(2), retryDelay: TimeSpan.FromSeconds(1));
        if (queue == null)
            return;

        using IQueue<SimpleWorkItem> workQueue = queue;
        await queue.DeleteQueueAsync();

        const int workItemCount = 1000;
        for (int i = 0; i < workItemCount; i++)
        {
            await queue.EnqueueAsync(new SimpleWorkItem
            {
                Data = "Hello"
            });
        }
        Assert.Equal(workItemCount, (await queue.GetQueueStatsAsync()).Queued);

        var workItem = await queue.DequeueAsync(TimeSpan.Zero);
        int work = 0;
        var sw = Stopwatch.StartNew();
        while (workItem != null)
        {
            Assert.Equal("Hello", workItem.Value.Data);
            await workItem.CompleteAsync();
            work++;

            workItem = await queue.DequeueAsync(TimeSpan.Zero);
        }
        sw.Stop();
        _logger.LogTrace("Work Items: {Count} Time: {Elapsed:g}", work, sw.Elapsed);

        var stats = await queue.GetQueueStatsAsync();
        Assert.Equal(workItemCount, stats.Dequeued);
        Assert.Equal(workItemCount, stats.Completed);
        Assert.Equal(0, stats.Queued);

        var muxer = SharedConnection.GetMuxer(Log);
        _logger.LogTrace("# Keys: {KeyCount}", await muxer.CountAllKeysAsync());
    }

    [Fact(Skip = "Performance Test")]
    public async Task MeasureWorkerThroughput()
    {
        var queue = GetQueue(retries: 3, workItemTimeout: TimeSpan.FromSeconds(2), retryDelay: TimeSpan.FromSeconds(1));
        if (queue == null)
            return;

        using IQueue<SimpleWorkItem> workQueue = queue;
        await queue.DeleteQueueAsync();

        const int workItemCount = 1;
        for (int i = 0; i < workItemCount; i++)
        {
            await queue.EnqueueAsync(new SimpleWorkItem
            {
                Data = "Hello"
            });
        }
        Assert.Equal(workItemCount, (await queue.GetQueueStatsAsync()).Queued);

        var countdown = new AsyncCountdownEvent(workItemCount);
        int work = 0;
        var sw = Stopwatch.StartNew();
        await queue.StartWorkingAsync(async workItem =>
        {
            Assert.Equal("Hello", workItem.Value.Data);
            await workItem.CompleteAsync();
            work++;
            countdown.Signal();
        }, cancellationToken: TestCancellationToken);

        await countdown.WaitAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(0, countdown.CurrentCount);
        sw.Stop();
        _logger.LogTrace("Work Items: {Count} Time: {Elapsed:g}", work, sw.Elapsed);

        var stats = await queue.GetQueueStatsAsync();
        Assert.Equal(workItemCount, stats.Dequeued);
        Assert.Equal(workItemCount, stats.Completed);
        Assert.Equal(0, stats.Queued);

        var muxer = SharedConnection.GetMuxer(Log);
        _logger.LogTrace("# Keys: {KeyCount}", await muxer.CountAllKeysAsync());
    }

    [Fact]
    public async Task CanHaveDifferentMessageTypeInQueueWithSameNameAsync()
    {
        await HandlerCommand1Async();
        await HandlerCommand2Async();

        await Task.Delay(1000, TestCancellationToken);

        await Publish1Async();
        await Publish2Async();
    }

    private IQueue<T> CreateQueue<T>(bool allQueuesTheSameName = true) where T : class
    {
        string name = typeof(T).FullName.Trim().Replace(".", String.Empty).ToLower();

        if (allQueuesTheSameName)
            name = "cmd";

        var queue = new RedisQueue<T>(o => o
            .ConnectionMultiplexer(SharedConnection.GetMuxer(Log))
            .Name(name)
            .LoggerFactory(Log)
        );

        _logger.LogDebug("Queue Id: {QueueId}", queue.QueueId);
        return queue;
    }

    private Task HandlerCommand1Async()
    {
        var q = CreateQueue<Command1>();

        return q.StartWorkingAsync((entry, _) =>
        {
            _logger.LogInformation("{UtcNow}: Handler1 {Name} {ValueId}", DateTime.UtcNow, entry.Value.GetType().Name, entry.Value.Id);
            Assert.InRange(entry.Value.Id, 100, 199);
            return Task.CompletedTask;
        });
    }

    private Task HandlerCommand2Async()
    {
        var q = CreateQueue<Command2>();

        return q.StartWorkingAsync((entry, _) =>
        {
            _logger.LogInformation("{UtcNow}: Handler2 {Name} {ValueId}", DateTime.UtcNow, entry.Value.GetType().Name, entry.Value.Id);
            Assert.InRange(entry.Value.Id, 200, 299);
            return Task.CompletedTask;
        }, true);
    }

    private async Task Publish1Async()
    {
        var q = CreateQueue<Command1>();

        for (int i = 0; i < 10; i++)
        {
            var cmd = new Command1(100 + i);
            _logger.LogInformation("{UtcNow}: Publish Command1 {CmdId}", DateTime.UtcNow, cmd.Id);
            await q.EnqueueAsync(cmd);
        }
    }

    private async Task Publish2Async()
    {
        var q = CreateQueue<Command2>();

        for (int i = 0; i < 10; i++)
        {
            var cmd = new Command2(200 + i);
            _logger.LogInformation("{UtcNow}: Publish Command2 {CmdId}", DateTime.UtcNow, cmd.Id);
            await q.EnqueueAsync(cmd);
        }
    }

    private record Command1(int Id);
    private record Command2(int Id);

    public ValueTask InitializeAsync()
    {
        _logger.LogDebug("Initializing");
        var muxer = SharedConnection.GetMuxer(Log);
        return new ValueTask(muxer.FlushAllAsync());
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Exceptionless;
using Foundatio.AsyncEx;
using Foundatio.Caching;
using Foundatio.Jobs;
using Foundatio.Lock;
using Foundatio.Messaging;
using Foundatio.Queues;
using Foundatio.Tests.Extensions;
using Foundatio.Xunit;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.Tests.Jobs;

public class WorkItemJobTests : TestWithLoggingBase
{
    public WorkItemJobTests(ITestOutputHelper output) : base(output) { }

    [Fact]
    public async Task CanRunWorkItem()
    {
        using var queue = new InMemoryQueue<WorkItemData>(o => o.LoggerFactory(Log));
        using var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        var handlerRegistry = new WorkItemHandlers();
        var job = new WorkItemJob(queue, messageBus, handlerRegistry, Log);

        handlerRegistry.Register<MyWorkItem>(async ctx =>
        {
            var jobData = ctx.GetData<MyWorkItem>();
            Assert.NotNull(jobData);
            Assert.Equal("Test", jobData.SomeData);

            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(100, TestCancellationToken);
                await ctx.ReportProgressAsync(10 * i);
            }
        });

        string jobId = await queue.EnqueueAsync(new MyWorkItem
        {
            SomeData = "Test"
        }, true);

        var countdown = new AsyncCountdownEvent(12);
        await messageBus.SubscribeAsync<WorkItemStatus>(status =>
        {
            _logger.LogInformation("Progress: {Progress}", status.Progress);
            Assert.Equal(jobId, status.WorkItemId);
            countdown.Signal();
        }, TestCancellationToken);

        await job.RunAsync(TestCancellationToken);
        await countdown.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, countdown.CurrentCount);
    }

    [Fact]
    public async Task CanHandleMultipleWorkItemInstances()
    {
        const int workItemCount = 1000;

        using var queue = new InMemoryQueue<WorkItemData>(o => o.RetryDelay(TimeSpan.Zero).Retries(0).LoggerFactory(Log));
        using var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        var handlerRegistry = new WorkItemHandlers();
        var j1 = new WorkItemJob(queue, messageBus, handlerRegistry, Log);
        var j2 = new WorkItemJob(queue, messageBus, handlerRegistry, Log);
        var j3 = new WorkItemJob(queue, messageBus, handlerRegistry, Log);
        int errors = 0;

        var jobIds = new ConcurrentDictionary<string, int>();

        handlerRegistry.Register<MyWorkItem>(async ctx =>
        {
            var jobData = ctx.GetData<MyWorkItem>();
            Assert.NotNull(jobData);
            Assert.Equal("Test", jobData.SomeData);

            int jobWorkTotal = jobIds.AddOrUpdate(ctx.JobId, 1, (key, value) => value + 1);
            if (jobData.Index % 100 == 0)
                _logger.LogTrace("Job {JobId} processing work item #: {JobWorkTotal}", ctx.JobId, jobWorkTotal);

            for (int i = 0; i < 10; i++)
                await ctx.ReportProgressAsync(10 * i);

            if (RandomData.GetBool(1))
            {
                Interlocked.Increment(ref errors);
                throw new Exception("Boom!");
            }
        });

        for (int i = 0; i < workItemCount; i++)
            await queue.EnqueueAsync(new MyWorkItem
            {
                SomeData = "Test",
                Index = i
            }, true);

        var completedItems = new List<string>();
        object completedItemsLock = new();
        await messageBus.SubscribeAsync<WorkItemStatus>(status =>
        {
            if (status.Progress == 100)
                _logger.LogTrace("Progress: {Progress}", status.Progress);

            if (status.Progress < 100)
                return;

            lock (completedItemsLock)
            {
                Assert.NotNull(status.WorkItemId);
                completedItems.Add(status.WorkItemId);
            }
        }, TestCancellationToken);

        using var cancellationTokenSource = new CancellationTokenSource(10000);
        List<Task> tasks =
        [
            Task.Run(async () =>
            {
                await j1.RunUntilEmptyAsync(cancellationTokenSource.Token);
                await cancellationTokenSource.CancelAsync();
            }, cancellationTokenSource.Token),

            Task.Run(async () =>
            {
                await j2.RunUntilEmptyAsync(cancellationTokenSource.Token);
                await cancellationTokenSource.CancelAsync();
            }, cancellationTokenSource.Token),

            Task.Run(async () =>
            {
                await j3.RunUntilEmptyAsync(cancellationTokenSource.Token);
                await cancellationTokenSource.CancelAsync();
            }, cancellationTokenSource.Token)
        ];

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogError(ex, "One or more tasks were cancelled: {Message}", ex.Message);
        }

        await Task.Delay(100, TestCancellationToken);
        _logger.LogInformation("Completed: {CompletedItems} Errors: {Errors}", completedItems.Count, errors);
        Assert.Equal(workItemCount, completedItems.Count + errors);
        Assert.Equal(3, jobIds.Count);
        Assert.Equal(workItemCount, jobIds.Sum(kvp => kvp.Value));
    }

    [Fact]
    public async Task CanRunWorkItemWithClassHandler()
    {
        using var queue = new InMemoryQueue<WorkItemData>(o => o.LoggerFactory(Log));
        using var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        var handlerRegistry = new WorkItemHandlers();
        var job = new WorkItemJob(queue, messageBus, handlerRegistry, Log);

        handlerRegistry.Register<MyWorkItem>(new MyWorkItemHandler(Log));

        string jobId = await queue.EnqueueAsync(new MyWorkItem
        {
            SomeData = "Test"
        }, true);

        var countdown = new AsyncCountdownEvent(11);
        await messageBus.SubscribeAsync<WorkItemStatus>(status =>
        {
            _logger.LogTrace("Progress: {Progress}", status.Progress);
            Assert.Equal(jobId, status.WorkItemId);
            countdown.Signal();
        }, TestCancellationToken);

        Assert.Equal(1, await job.RunUntilEmptyAsync(cancellationToken: TestCancellationToken));
        await countdown.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, countdown.CurrentCount);
    }

    [Fact]
    public async Task CanRunWorkItemWithDelegateHandler()
    {
        using var queue = new InMemoryQueue<WorkItemData>(o => o.LoggerFactory(Log));
        using var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        var handlerRegistry = new WorkItemHandlers();
        var job = new WorkItemJob(queue, messageBus, handlerRegistry, Log);

        handlerRegistry.Register<MyWorkItem>(async ctx =>
        {
            var jobData = ctx.GetData<MyWorkItem>();
            Assert.NotNull(jobData);
            Assert.Equal("Test", jobData.SomeData);

            for (int i = 1; i < 10; i++)
            {
                await Task.Delay(100, TestCancellationToken);
                await ctx.ReportProgressAsync(10 * i);
            }
        }, Log.CreateLogger("MyWorkItem"));

        string jobId = await queue.EnqueueAsync(new MyWorkItem
        {
            SomeData = "Test"
        }, true);

        var countdown = new AsyncCountdownEvent(11);
        await messageBus.SubscribeAsync<WorkItemStatus>(status =>
        {
            _logger.LogTrace("Progress: {Progress}", status.Progress);
            Assert.Equal(jobId, status.WorkItemId);
            countdown.Signal();
        }, TestCancellationToken);

        Assert.Equal(1, await job.RunUntilEmptyAsync(cancellationToken: TestCancellationToken));
        await countdown.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, countdown.CurrentCount);
    }

    [Fact]
    public async Task CanRunWorkItemJobUntilEmpty()
    {
        using var queue = new InMemoryQueue<WorkItemData>(o => o.LoggerFactory(Log));
        using var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        var handlerRegistry = new WorkItemHandlers();
        var job = new WorkItemJob(queue, messageBus, handlerRegistry, Log);

        handlerRegistry.Register<MyWorkItem>(new MyWorkItemHandler(Log));

        await queue.EnqueueAsync(new MyWorkItem
        {
            SomeData = "Test"
        }, true);

        await queue.EnqueueAsync(new MyWorkItem
        {
            SomeData = "Test"
        }, true);

        Assert.Equal(2, await job.RunUntilEmptyAsync(cancellationToken: TestCancellationToken));
        var stats = await queue.GetQueueStatsAsync();
        Assert.Equal(2, stats.Enqueued);
        Assert.Equal(2, stats.Dequeued);
        Assert.Equal(2, stats.Completed);
    }

    [Fact]
    public async Task CanRunWorkItemJobUntilEmptyWithNoEnqueuedItems()
    {
        using var queue = new InMemoryQueue<WorkItemData>(o => o.LoggerFactory(Log));
        using var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        var handlerRegistry = new WorkItemHandlers();
        var job = new WorkItemJob(queue, messageBus, handlerRegistry, Log);

        handlerRegistry.Register<MyWorkItem>(new MyWorkItemHandler(Log));

        var sw = Stopwatch.StartNew();
        Assert.Equal(0, await job.RunUntilEmptyAsync(TimeSpan.FromMilliseconds(100), TestCancellationToken));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(250));

        var stats = await queue.GetQueueStatsAsync();
        Assert.Equal(0, stats.Enqueued);
        Assert.Equal(0, stats.Dequeued);
        Assert.Equal(0, stats.Completed);
    }

    [Fact]
    public async Task CanRunWorkItemJobUntilEmptyHandlesCancellation()
    {
        using var queue = new InMemoryQueue<WorkItemData>(o => o.LoggerFactory(Log));
        using var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        var handlerRegistry = new WorkItemHandlers();
        var job = new WorkItemJob(queue, messageBus, handlerRegistry, Log);

        handlerRegistry.Register<MyWorkItem>(new MyWorkItemHandler(Log));

        await queue.EnqueueAsync(new MyWorkItem
        {
            SomeData = "Test"
        }, true);

        await queue.EnqueueAsync(new MyWorkItem
        {
            SomeData = "Test"
        }, true);

        Assert.Equal(1, await job.RunUntilEmptyAsync(TimeSpan.FromMilliseconds(50), TestCancellationToken));

        var stats = await queue.GetQueueStatsAsync();
        Assert.Equal(2, stats.Enqueued);
        Assert.Equal(1, stats.Dequeued);
        Assert.Equal(1, stats.Completed);
    }

    [Fact]
    public async Task CanRunBadWorkItem()
    {
        using var queue = new InMemoryQueue<WorkItemData>(o => o.RetryDelay(TimeSpan.FromMilliseconds(500)).LoggerFactory(Log));
        using var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        var handlerRegistry = new WorkItemHandlers();
        var job = new WorkItemJob(queue, messageBus, handlerRegistry, Log);

        handlerRegistry.Register<MyWorkItem>(ctx =>
        {
            var jobData = ctx.GetData<MyWorkItem>();
            Assert.NotNull(jobData);
            Assert.Equal("Test", jobData.SomeData);
            throw new Exception();
        });

        string jobId = await queue.EnqueueAsync(new MyWorkItem
        {
            SomeData = "Test"
        }, true);

        var countdown = new AsyncCountdownEvent(2);
        await messageBus.SubscribeAsync<WorkItemStatus>(status =>
        {
            _logger.LogTrace("Progress: {Progress}", status.Progress);
            Assert.Equal(jobId, status.WorkItemId);
            countdown.Signal();
        }, TestCancellationToken);

        Assert.Equal(0, await job.RunUntilEmptyAsync(cancellationToken: TestCancellationToken));
        await countdown.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, countdown.CurrentCount);
    }

    [Theory]
    [InlineData(LostLeaseResponse.Throw)]
    [InlineData(LostLeaseResponse.ReturnEarly)]
    [InlineData(LostLeaseResponse.SwallowCancellation)]
    public async Task ProcessAsync_WhenAutoRenewedWorkItemLockIsLost_CancelsHandlerAndAbandonsEntry(LostLeaseResponse response)
    {
        // Arrange
        using var cache = new InMemoryCacheClient(o => o.LoggerFactory(Log));
        using var queue = new InMemoryQueue<WorkItemData>(o => o.RetryDelay(TimeSpan.Zero).Retries(0).LoggerFactory(Log));
        using var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        var locker = new CacheLockProvider(cache, messageBus, null, null, Log);
        var handler = new LockedWorkItemHandler(locker, Log, response: response) { AutoRenewLockOnProgress = true };
        var handlerRegistry = new WorkItemHandlers();
        handlerRegistry.Register<MyWorkItem>(handler);
        var job = new WorkItemJob(queue, messageBus, handlerRegistry, Log);
        await queue.EnqueueAsync(new MyWorkItem { SomeData = "Test" }, true);

        // Act
        var result = await job.RunAsync(TestCancellationToken);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.True(handler.ObservedCancellation);
        Assert.False(handler.CompletedWork);
        var stats = await queue.GetQueueStatsAsync();
        Assert.Equal(0, stats.Completed);
        Assert.Equal(1, stats.Abandoned);
    }

    [Fact]
    public async Task ProcessAsync_WhenAutoRenewedWorkItemLockIsHeld_DoesNotCancelHandler()
    {
        // Arrange
        using var cache = new InMemoryCacheClient(o => o.LoggerFactory(Log));
        using var queue = new InMemoryQueue<WorkItemData>(o => o.LoggerFactory(Log));
        using var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        var locker = new CacheLockProvider(cache, messageBus, null, null, Log);
        var handler = new LockedWorkItemHandler(locker, Log, loseLock: false) { AutoRenewLockOnProgress = true };
        var handlerRegistry = new WorkItemHandlers();
        handlerRegistry.Register<MyWorkItem>(handler);
        var job = new WorkItemJob(queue, messageBus, handlerRegistry, Log);
        await queue.EnqueueAsync(new MyWorkItem { SomeData = "Test" }, true);

        // Act
        var result = await job.RunAsync(TestCancellationToken);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.False(handler.ObservedCancellation);
        Assert.True(handler.CompletedWork);
        var stats = await queue.GetQueueStatsAsync();
        Assert.Equal(1, stats.Completed);
    }

    [Fact]
    public async Task ProcessAsync_WhenJobIsCancelledAfterWorkItemLockIsLost_AbandonsEntry()
    {
        // Arrange
        using var cache = new InMemoryCacheClient(o => o.LoggerFactory(Log));
        using var queue = new InMemoryQueue<WorkItemData>(o => o.RetryDelay(TimeSpan.Zero).Retries(0).LoggerFactory(Log));
        using var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        var locker = new CacheLockProvider(cache, messageBus, null, null, Log);
        using var jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestCancellationToken);
        var handler = new LockedWorkItemHandler(locker, Log, response: LostLeaseResponse.ReturnEarly, cancelAfterLoss: jobCancellation) { AutoRenewLockOnProgress = true };
        var handlerRegistry = new WorkItemHandlers();
        handlerRegistry.Register<MyWorkItem>(handler);
        var job = new WorkItemJob(queue, messageBus, handlerRegistry, Log);
        await queue.EnqueueAsync(new MyWorkItem { SomeData = "Test" }, true);

        // Act
        var result = await job.RunAsync(jobCancellation.Token);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.True(handler.ObservedCancellation);
        Assert.False(handler.CompletedWork);
        var stats = await queue.GetQueueStatsAsync();
        Assert.Equal(0, stats.Completed);
        Assert.Equal(1, stats.Abandoned);
    }
}

public enum LostLeaseResponse
{
    Throw,
    ReturnEarly,
    SwallowCancellation
}

public class LockedWorkItemHandler : WorkItemHandlerBase
{
    private readonly ILockProvider _locker;
    private readonly bool _loseLock;
    private readonly LostLeaseResponse _response;
    private readonly CancellationTokenSource? _cancelAfterLoss;

    public LockedWorkItemHandler(ILockProvider locker, ILoggerFactory loggerFactory, bool loseLock = true, LostLeaseResponse response = LostLeaseResponse.Throw, CancellationTokenSource? cancelAfterLoss = null) : base(loggerFactory)
    {
        _locker = locker;
        _loseLock = loseLock;
        _response = response;
        _cancelAfterLoss = cancelAfterLoss;
    }

    public bool ObservedCancellation { get; private set; }
    public bool CompletedWork { get; private set; }

    public override Task<ILock?> GetWorkItemLockAsync(object workItem, CancellationToken cancellationToken = default)
    {
        return _locker.TryAcquireAsync("work-item", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken);
    }

    public override async Task HandleItemAsync(WorkItemContext context)
    {
        Assert.NotNull(context.WorkItemLock);
        if (_loseLock)
            await _locker.ReleaseAsync(context.WorkItemLock.Resource);

        await context.ReportProgressAsync(50);
        ObservedCancellation = context.CancellationToken.IsCancellationRequested;
        if (_cancelAfterLoss is not null)
            await _cancelAfterLoss.CancelAsync();

        switch (_response)
        {
            case LostLeaseResponse.ReturnEarly when ObservedCancellation:
                return;
            case LostLeaseResponse.SwallowCancellation:
                try
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                break;
            default:
                context.CancellationToken.ThrowIfCancellationRequested();
                break;
        }

        CompletedWork = true;
    }
}

public class MyWorkItem
{
    public required string SomeData { get; set; }
    public int Index { get; set; }
}

public class MyWorkItemHandler : WorkItemHandlerBase
{
    public MyWorkItemHandler(ILoggerFactory? loggerFactory = null) : base(loggerFactory)
    {
    }

    public override async Task HandleItemAsync(WorkItemContext context)
    {
        var jobData = context.GetData<MyWorkItem>();
        Assert.NotNull(jobData);
        Assert.Equal("Test", jobData.SomeData);

        for (int i = 1; i < 10; i++)
        {
            await Task.Delay(10);
            await context.ReportProgressAsync(10 * i);
        }
    }
}

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
using Foundatio.Utility;
using Foundatio.Xunit;
using Microsoft.Extensions.Logging;
using Moq;
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

    [Fact]
    public async Task ProcessAsync_WhenAbandonmentObserverThrows_SendsOneFailureForTheAttempt()
    {
        // Arrange
        using var cache = new InMemoryCacheClient(o => o.LoggerFactory(Log));
        using var queue = new InMemoryQueue<WorkItemData>(o => o.LoggerFactory(Log).Retries(0));
        using var bus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        var locker = new CacheLockProvider(cache, bus, Log);
        await using var lease = await locker.AcquireAsync("item", cancellationToken: TestCancellationToken);
        var observerError = new InvalidOperationException("Abandonment observer failed");
        using var observer = queue.Abandoned.AddHandler((_, _) => throw observerError);
        var statuses = new ConcurrentQueue<int>();
        await bus.SubscribeAsync<WorkItemStatus>(s => statuses.Enqueue(s.Progress), TestCancellationToken);
        var handlers = new WorkItemHandlers();
        handlers.Register<string>(new RenewingWorkItemHandler(lease, async ctx =>
        {
            await locker.ReleaseAsync(lease.Resource);
            try
            {
                await ctx.ReportProgressAsync(50);
            }
            catch (LockException)
            {
                return;
            }
        }, _logger));
        await queue.EnqueueAsync("item", true);
        var job = new WorkItemJob(queue, bus, handlers, Log);

        // Act
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => job.RunAsync(TestCancellationToken));

        // Assert
        Assert.Same(observerError, error);
        Assert.Equal([0, -1], statuses);
        Assert.Equal(1, (await queue.GetQueueStatsAsync()).Abandoned);
    }

    [Fact]
    public async Task ProcessAsync_WhenAutoRenewedWorkItemLockIsHeld_DoesNotCancelHandler()
    {
        // Arrange
        using var cache = new InMemoryCacheClient(o => o.LoggerFactory(Log));
        using var queue = new InMemoryQueue<WorkItemData>(o => o.LoggerFactory(Log));
        using var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        var locker = new CacheLockProvider(cache, messageBus, Log);
        await using var lease = await locker.AcquireAsync("work-item", cancellationToken: TestCancellationToken);
        var handlerRegistry = new WorkItemHandlers();
        handlerRegistry.Register<MyWorkItem>(new RenewingWorkItemHandler(lease, async context =>
        {
            await context.ReportProgressAsync(50);
            context.CancellationToken.ThrowIfCancellationRequested();
        }, _logger));
        var job = new WorkItemJob(queue, messageBus, handlerRegistry, Log);
        await queue.EnqueueAsync(new MyWorkItem { SomeData = "Test" }, true);

        // Act
        var result = await job.RunAsync(TestCancellationToken);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(1, lease.RenewalCount);
        var stats = await queue.GetQueueStatsAsync();
        Assert.Equal(1, stats.Completed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessAsync_WhenAutoRenewedWorkItemLockIsLost_CancelsBeforeQueueRenewalCompletes(bool queueRenewalFails)
    {
        // Arrange
        using var cache = new InMemoryCacheClient(o => o.LoggerFactory(Log));
        using var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        using var queue = new InMemoryQueue<WorkItemData>(o => o.LoggerFactory(Log).RetryDelay(TimeSpan.Zero).Retries(0));
        await queue.EnqueueAsync(new MyWorkItem { SomeData = "Test" });
        var entry = await queue.DequeueAsync(TestCancellationToken);
        Assert.NotNull(entry);

        var queueRenewal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var observer = queue.LockRenewed.AddHandler((_, _) => queueRenewal.Task);
        var locker = new CacheLockProvider(cache, messageBus, Log);
        await using var workItemLock = await locker.AcquireAsync("work-item", cancellationToken: TestCancellationToken);
        await workItemLock.ReleaseAsync();
        var handlers = new WorkItemHandlers();
        handlers.Register<MyWorkItem>(new RenewingWorkItemHandler(workItemLock, async context =>
        {
            using var registration = context.CancellationToken.Register(() => cancellationObserved.TrySetResult());
            await context.ReportProgressAsync(50);
        }, _logger));
        var job = new WorkItemJob(queue, messageBus, handlers, Log);

        // Act
        var processing = job.ProcessAsync(entry, TestCancellationToken);
        try
        {
            await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);

            // Assert
            Assert.False(queueRenewal.Task.IsCompleted);
            Assert.False(processing.IsCompleted);
        }
        finally
        {
            if (queueRenewalFails)
                queueRenewal.TrySetException(new InvalidOperationException("Queue renewal failed"));
            else
                queueRenewal.TrySetResult();

            await processing.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);
        }

        Assert.False((await processing).IsSuccess);
        Assert.True(entry.IsAbandoned);
        Assert.False(entry.IsCompleted);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task ProcessAsync_WhenAutoRenewedWorkItemLockIsLost_CancelsHandlerAndAbandonsEntry(bool catchRenewalError, bool multipleResources, bool sendProgressReports)
    {
        // Arrange
        using var cache = new InMemoryCacheClient(o => o.LoggerFactory(Log));
        using var queue = new InMemoryQueue<WorkItemData>(o => o.RetryDelay(TimeSpan.Zero).Retries(0).LoggerFactory(Log));
        using var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        var locker = new CacheLockProvider(cache, messageBus, Log);
        await using var lease = multipleResources
            ? await locker.AcquireAsync(["work-item", "other"], cancellationToken: TestCancellationToken)
            : await locker.AcquireAsync("work-item", cancellationToken: TestCancellationToken);
        bool observedCancellation = false;
        bool continuedAfterReport = false;
        var handlerRegistry = new WorkItemHandlers();
        handlerRegistry.Register<MyWorkItem>(new RenewingWorkItemHandler(lease, async context =>
        {
            await locker.ReleaseAsync("work-item");
            await using var replacement = await locker.AcquireAsync("work-item", cancellationToken: TestCancellationToken);
            try
            {
                await context.ReportProgressAsync(100);
                continuedAfterReport = true;
            }
            catch (LockException) when (catchRenewalError)
            {
                await Assert.ThrowsAsync<LockException>(() => context.ReportProgressAsync(100));
            }
            finally
            {
                observedCancellation = context.CancellationToken.IsCancellationRequested;
            }
        }, _logger));
        var job = new WorkItemJob(queue, messageBus, handlerRegistry, Log);
        await queue.EnqueueAsync(new MyWorkItem { SomeData = "Test" }, sendProgressReports);
        var entry = await queue.DequeueAsync(TestCancellationToken);
        Assert.NotNull(entry);
        var statuses = new ConcurrentQueue<(WorkItemStatus Status, bool IsAbandoned)>();
        await messageBus.SubscribeAsync<WorkItemStatus>(status => statuses.Enqueue((status, entry.IsAbandoned)), TestCancellationToken);

        // Act
        var result = await job.ProcessAsync(entry, TestCancellationToken);

        // Assert
        Assert.IsType<LockException>(result.Error);
        Assert.False(continuedAfterReport);
        Assert.True(observedCancellation);
        var stats = await queue.GetQueueStatsAsync();
        Assert.Equal(0, stats.Completed);
        Assert.Equal(1, stats.Abandoned);
        Assert.Equal(sendProgressReports ? [0, -1] : Array.Empty<int>(), statuses.Select(status => status.Status.Progress));
        if (sendProgressReports)
        {
            var failure = Assert.Single(statuses, status => status.Status.Progress is -1);
            Assert.StartsWith("Failed:", failure.Status.Message);
            Assert.True(failure.IsAbandoned);
        }
    }

    [Fact]
    public async Task ProcessAsync_WhenCompletionObserverThrows_ReportsCompletedEntry()
    {
        // Arrange
        using var queue = new InMemoryQueue<WorkItemData>(o => o.LoggerFactory(Log));
        using var bus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        var error = new InvalidOperationException("Completion observer failed");
        using var observer = queue.Completed.AddHandler((_, _) => throw error);
        var statuses = new ConcurrentQueue<int>();
        await bus.SubscribeAsync<WorkItemStatus>(s => statuses.Enqueue(s.Progress), TestCancellationToken);
        var handlers = new WorkItemHandlers();
        handlers.Register<string>(_ => Task.CompletedTask);
        await queue.EnqueueAsync("item", true);

        // Act
        var result = await new WorkItemJob(queue, bus, handlers, Log).RunAsync(TestCancellationToken);

        // Assert
        Assert.Same(error, result.Error);
        Assert.Equal(1, (await queue.GetQueueStatsAsync()).Completed);
        Assert.Equal([0, 100], statuses);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ProcessAsync_WhenEntryIsRedelivered_PreservesRetryOutcome(bool renewOnProgress, bool redeliverDuringRenewal)
    {
        // Arrange
        using var queue = new InMemoryQueue<WorkItemData>(o => o.LoggerFactory(Log).RetryDelay(TimeSpan.Zero));
        using var bus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        await queue.EnqueueAsync("item", true);
        var entry = await queue.DequeueAsync(TestCancellationToken);
        Assert.NotNull(entry);
        var statuses = new ConcurrentQueue<int>();
        await bus.SubscribeAsync<WorkItemStatus>(s => statuses.Enqueue(s.Progress), TestCancellationToken);
        var retryHandlers = new WorkItemHandlers();
        retryHandlers.Register<string>(_ => Task.CompletedTask);
        var retryJob = new WorkItemJob(queue, bus, retryHandlers, Log);
        async Task RedeliverAsync()
        {
            await entry.AbandonAsync();
            await retryJob.RunAsync(TestCancellationToken);
        }

        using var observer = queue.LockRenewed.AddHandler((_, e) =>
            redeliverDuringRenewal && ReferenceEquals(e.Entry, entry) ? RedeliverAsync() : Task.CompletedTask);
        var handlers = new WorkItemHandlers();
        handlers.Register<string>(new DelegateWorkItemHandler(async ctx =>
        {
            if (!redeliverDuringRenewal)
                await RedeliverAsync();

            await ctx.ReportProgressAsync(50);
        }, _logger)
        { AutoRenewLockOnProgress = renewOnProgress });

        // Act
        var result = await new WorkItemJob(queue, bus, handlers, Log).ProcessAsync(entry, TestCancellationToken);

        // Assert
        Assert.False(result.IsSuccess);
        if (renewOnProgress)
            Assert.IsType<InvalidOperationException>(result.Error);
        else
            Assert.Null(result.Error);

        Assert.Equal([0, 0, 100], statuses);
        var stats = await queue.GetQueueStatsAsync();
        Assert.Equal(1, stats.Abandoned);
        Assert.Equal(1, stats.Completed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessAsync_WhenExplicitRenewalFailureIsCaught_AbandonsWithProviderError(bool callbackThrows)
    {
        // Arrange
        using var cache = new InMemoryCacheClient(o => o.LoggerFactory(Log));
        using var queue = new InMemoryQueue<WorkItemData>(o => o.LoggerFactory(Log).Retries(0));
        using var bus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        var locker = new CacheLockProvider(cache, bus, Log);
        await using var lease = await locker.AcquireAsync("item", cancellationToken: TestCancellationToken);
        LockException? renewalError = null;
        var handlers = new WorkItemHandlers();
        handlers.Register<string>(new RenewingWorkItemHandler(lease, async ctx =>
        {
            using var registration = ctx.CancellationToken.Register(() =>
            {
                if (callbackThrows)
                    throw new InvalidOperationException("Cancellation observer failed");
            });
            await locker.ReleaseAsync(lease.Resource);
            renewalError = await Assert.ThrowsAsync<LockException>(() => ctx.RenewLockAsync());
            Assert.True(ctx.CancellationToken.IsCancellationRequested);
        }, _logger)
        { AutoRenewLockOnProgress = false });
        await queue.EnqueueAsync("item");

        // Act
        var result = await new WorkItemJob(queue, bus, handlers, Log).RunAsync(TestCancellationToken);

        // Assert
        Assert.NotNull(renewalError);
        Assert.Same(renewalError, result.Error);
        Assert.Equal(1, (await queue.GetQueueStatsAsync()).Abandoned);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ProcessAsync_WhenHandlerExitsWithPendingProgress_WaitsBeforeFinishing(bool handlerThrows, bool lockIsLost)
    {
        // Arrange
        using var queue = new InMemoryQueue<WorkItemData>(o => o.RetryDelay(TimeSpan.Zero).Retries(0).LoggerFactory(Log));
        await queue.EnqueueAsync(new MyWorkItem { SomeData = "Test" }, true);
        var entry = await queue.DequeueAsync(TestCancellationToken);
        Assert.NotNull(entry);

        var statuses = new ConcurrentQueue<WorkItemStatus>();
        using var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        await messageBus.SubscribeAsync<WorkItemStatus>(status => statuses.Enqueue(status), TestCancellationToken);

        var firstRenewal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRenewal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workItemLock = new Mock<ILock>();
        workItemLock.SetupSequence(l => l.RenewAsync(It.IsAny<TimeSpan?>()))
            .Returns(firstRenewal.Task)
            .Returns(secondRenewal.Task);
        workItemLock.Setup(l => l.ReleaseAsync()).Returns(Task.CompletedTask);
        WorkItemContext? workItemContext = null;
        var progressStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task firstProgress = Task.CompletedTask;
        Task secondProgress = Task.CompletedTask;
        var handlerError = new InvalidOperationException("Handler failed");
        var handlers = new WorkItemHandlers();
        handlers.Register<MyWorkItem>(new RenewingWorkItemHandler(workItemLock.Object, context =>
        {
            workItemContext = context;
            firstProgress = context.ReportProgressAsync(25);
            secondProgress = context.ReportProgressAsync(50);
            progressStarted.SetResult();
            return handlerThrows ? Task.FromException(handlerError) : Task.CompletedTask;
        }, _logger));
        var job = new WorkItemJob(queue, messageBus, handlers, Log);

        // Act
        var processing = job.ProcessAsync(entry, TestCancellationToken);
        try
        {
            await progressStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);

            // Assert
            Assert.False(processing.IsCompleted);
            Assert.False(entry.IsCompleted);
            Assert.False(entry.IsAbandoned);
            workItemLock.Verify(l => l.ReleaseAsync(), Times.Never);

            secondRenewal.SetResult();
            await secondProgress.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);
            Assert.False(processing.IsCompleted);
            Assert.False(entry.IsCompleted);
            Assert.False(entry.IsAbandoned);
            workItemLock.Verify(l => l.ReleaseAsync(), Times.Never);
        }
        finally
        {
            secondRenewal.TrySetResult();
            if (lockIsLost)
            {
                firstRenewal.TrySetException(new CacheException("Renewal unavailable"));
                await Assert.ThrowsAsync<CacheException>(() => firstProgress);
            }
            else
            {
                firstRenewal.TrySetResult();
                await firstProgress.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);
            }

            await Task.WhenAll(processing, secondProgress).WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);
        }

        bool succeeds = !handlerThrows && !lockIsLost;
        var result = await processing;
        Assert.Equal(succeeds, result.IsSuccess);
        if (handlerThrows)
            Assert.Same(handlerError, result.Error);
        else if (lockIsLost)
            Assert.IsType<CacheException>(result.Error);
        else
            Assert.Null(result.Error);

        Assert.Equal(succeeds, entry.IsCompleted);
        Assert.Equal(!succeeds, entry.IsAbandoned);
        Assert.NotNull(workItemContext);
        Assert.Equal(lockIsLost, workItemContext.CancellationToken.IsCancellationRequested);
        Assert.Equal(0, statuses.First().Progress);
        Assert.Equal(succeeds ? 100 : -1, statuses.Last().Progress);
        if (lockIsLost)
            Assert.DoesNotContain(statuses, status => status.Progress == 25);

        workItemLock.Verify(l => l.ReleaseAsync(), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_WhenJobIsCancelledAfterWorkItemLockIsLost_AbandonsEntry()
    {
        // Arrange
        using var cache = new InMemoryCacheClient(o => o.LoggerFactory(Log));
        using var queue = new InMemoryQueue<WorkItemData>(o => o.RetryDelay(TimeSpan.Zero).Retries(0).LoggerFactory(Log));
        using var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        var locker = new CacheLockProvider(cache, messageBus, Log);
        using var jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestCancellationToken);
        await using var lease = await locker.AcquireAsync("work-item", cancellationToken: TestCancellationToken);
        bool cancelledBeforeHost = false;
        var handlerRegistry = new WorkItemHandlers();
        handlerRegistry.Register<MyWorkItem>(new RenewingWorkItemHandler(lease, async context =>
        {
            await locker.ReleaseAsync(lease.Resource);
            await Assert.ThrowsAsync<LockException>(() => context.ReportProgressAsync(50));
            cancelledBeforeHost = context.CancellationToken.IsCancellationRequested;
            await jobCancellation.CancelAsync();
        }, _logger));
        var job = new WorkItemJob(queue, messageBus, handlerRegistry, Log);
        await queue.EnqueueAsync(new MyWorkItem { SomeData = "Test" }, true);

        // Act
        var result = await job.RunAsync(jobCancellation.Token);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.True(cancelledBeforeHost);
        var stats = await queue.GetQueueStatsAsync();
        Assert.Equal(0, stats.Completed);
        Assert.Equal(1, stats.Abandoned);
    }

    [Fact]
    public async Task ProcessAsync_WhenProgressIsReportedAfterCompletion_DoesNotRenewOrPublish()
    {
        // Arrange
        using var cache = new InMemoryCacheClient(o => o.LoggerFactory(Log));
        using var queue = new InMemoryQueue<WorkItemData>(o => o.LoggerFactory(Log));
        using var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        var locker = new CacheLockProvider(cache, messageBus, Log);
        await using var lease = await locker.AcquireAsync("work-item", cancellationToken: TestCancellationToken);
        var statuses = new ConcurrentQueue<int>();
        await messageBus.SubscribeAsync<WorkItemStatus>(s => statuses.Enqueue(s.Progress), TestCancellationToken);
        await queue.EnqueueAsync(new MyWorkItem { SomeData = "Test" }, true);
        var entry = await queue.DequeueAsync(TestCancellationToken);
        Assert.NotNull(entry);

        WorkItemContext? workItemContext = null;
        var handlers = new WorkItemHandlers();
        handlers.Register<MyWorkItem>(new RenewingWorkItemHandler(lease, context =>
        {
            workItemContext = context;
            return Task.CompletedTask;
        }, _logger));
        var job = new WorkItemJob(queue, messageBus, handlers, Log);

        Assert.True((await job.ProcessAsync(entry, TestCancellationToken)).IsSuccess);
        Assert.NotNull(workItemContext);

        // Act
        await workItemContext.ReportProgressAsync(50);

        // Assert
        Assert.Equal([0, 100], statuses);
        Assert.Equal(0, lease.RenewalCount);
        Assert.False(await locker.IsLockedAsync("work-item"));
        Assert.True(entry.IsCompleted);
    }

    [Fact]
    public async Task ProcessAsync_WhenQueueRenewalFails_DoesNotCancelHandler()
    {
        // Arrange
        using var cache = new InMemoryCacheClient(o => o.LoggerFactory(Log));
        using var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        using var queue = new InMemoryQueue<WorkItemData>(o => o.LoggerFactory(Log));
        await queue.EnqueueAsync(new MyWorkItem { SomeData = "Test" });
        var entry = await queue.DequeueAsync(TestCancellationToken);
        Assert.NotNull(entry);
        using var observer = queue.LockRenewed.AddHandler((_, _) => throw new InvalidOperationException("Queue renewal failed"));
        var locker = new CacheLockProvider(cache, messageBus, Log);
        await using var lease = await locker.AcquireAsync("work-item", cancellationToken: TestCancellationToken);
        var handlers = new WorkItemHandlers();
        handlers.Register<MyWorkItem>(new RenewingWorkItemHandler(lease, async context =>
        {
            await context.ReportProgressAsync(50);
            context.CancellationToken.ThrowIfCancellationRequested();
        }, _logger));
        var job = new WorkItemJob(queue, messageBus, handlers, Log);

        // Act
        var result = await job.ProcessAsync(entry, TestCancellationToken);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(1, lease.RenewalCount);
        Assert.True(entry.IsCompleted);
        Assert.False(entry.IsAbandoned);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessAsync_WhenReleaseFails_ReportsSettledOutcome(bool renewalFails)
    {
        // Arrange
        using var cache = new InMemoryCacheClient(o => o.LoggerFactory(Log));
        using var lockBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        using var statusBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        using var queue = new InMemoryQueue<WorkItemData>(o => o.LoggerFactory(Log).Retries(0));
        var locker = new CacheLockProvider(cache, lockBus, Log);
        await using var lease = await locker.AcquireAsync("item", cancellationToken: TestCancellationToken);
        if (renewalFails)
            await locker.ReleaseAsync(lease.Resource);

        var statuses = new ConcurrentQueue<int>();
        await statusBus.SubscribeAsync<WorkItemStatus>(status => statuses.Enqueue(status.Progress), TestCancellationToken);
        var handlers = new WorkItemHandlers();
        handlers.Register<string>(new RenewingWorkItemHandler(lease, async context =>
        {
            lockBus.Dispose();
            if (renewalFails)
                await context.RenewLockAsync();
        }, _logger));
        await queue.EnqueueAsync("item", true);
        var entry = await queue.DequeueAsync(TestCancellationToken);
        Assert.NotNull(entry);
        var job = new WorkItemJob(queue, statusBus, handlers, Log);

        // Act
        await Assert.ThrowsAsync<MessageBusException>(() => job.ProcessAsync(entry, TestCancellationToken));

        // Assert
        Assert.Equal(renewalFails, entry.IsAbandoned);
        Assert.Equal(!renewalFails, entry.IsCompleted);
        Assert.Equal([0, renewalFails ? -1 : 100], statuses);
    }

    [Fact]
    public async Task ProcessAsync_WhenResultIsFailure_AbandonsAndReportsFailure()
    {
        // Arrange
        using var queue = new InMemoryQueue<WorkItemData>(o => o.LoggerFactory(Log).Retries(0));
        using var bus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        var statuses = new ConcurrentQueue<WorkItemStatus>();
        await bus.SubscribeAsync<WorkItemStatus>(status => statuses.Enqueue(status), TestCancellationToken);
        var handlers = new WorkItemHandlers();
        handlers.Register<string>(ctx =>
        {
            ctx.Result = JobResult.FailedWithMessage("Downstream rejected the item");
            return Task.CompletedTask;
        });
        await queue.EnqueueAsync("item", true);

        // Act
        var result = await new WorkItemJob(queue, bus, handlers, Log).RunAsync(TestCancellationToken);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal([0, -1], statuses.Select(status => status.Progress));
        Assert.Equal("Failed: Downstream rejected the item", statuses.Last().Message);
        Assert.Equal(1, (await queue.GetQueueStatsAsync()).Abandoned);
    }

    [Theory]
    [InlineData(false, 50)]
    [InlineData(true, 50)]
    [InlineData(true, 100)]
    [InlineData(true, -1)]
    public async Task ProcessAsync_WhenStatusDeliveryIsPending_SettlesAndReleasesBeforeDelivery(bool renewOnProgress, int blockedStatus)
    {
        // Arrange
        using var cache = new InMemoryCacheClient(o => o.LoggerFactory(Log));
        using var queue = new InMemoryQueue<WorkItemData>(o => o.LoggerFactory(Log).Retries(0));
        using var bus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        var locker = new CacheLockProvider(cache, bus, Log);
        await using var lease = await locker.AcquireAsync("item", cancellationToken: TestCancellationToken);
        await queue.EnqueueAsync("item", true);
        var entry = await queue.DequeueAsync(TestCancellationToken);
        Assert.NotNull(entry);
        var delivering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await bus.SubscribeAsync<WorkItemStatus>(async status =>
        {
            if (status.Progress == blockedStatus)
            {
                delivering.TrySetResult();
                await resume.Task.WaitAsync(TestCancellationToken);
            }
        }, TestCancellationToken);

        Task report = Task.CompletedTask;
        var handlers = new WorkItemHandlers();
        handlers.Register<string>(new RenewingWorkItemHandler(lease, ctx =>
        {
            if (blockedStatus == -1)
                throw new InvalidOperationException("Handler failed");

            report = ctx.ReportProgressAsync(50);
            return Task.CompletedTask;
        }, _logger)
        { AutoRenewLockOnProgress = renewOnProgress });

        // Act
        var processing = new WorkItemJob(queue, bus, handlers, Log).ProcessAsync(entry, TestCancellationToken);
        try
        {
            await delivering.Task.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);
            if (blockedStatus == 50)
                await processing.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);

            // Assert
            Assert.Equal(blockedStatus == -1, entry.IsAbandoned);
            Assert.Equal(blockedStatus != -1, entry.IsCompleted);
            Assert.False(await locker.IsLockedAsync("item"));
        }
        finally
        {
            resume.TrySetResult();
            await Task.WhenAll(processing, report).WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);
        }
    }

    private sealed class RenewingWorkItemHandler : DelegateWorkItemHandler
    {
        private readonly ILock _lease;

        public RenewingWorkItemHandler(ILock lease, Func<WorkItemContext, Task> handle, ILogger logger) : base(handle, logger)
        {
            _lease = lease;
            AutoRenewLockOnProgress = true;
        }

        public override Task<ILock?> GetWorkItemLockAsync(object workItem, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<ILock?>(_lease);
        }
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

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Jobs;
using Foundatio.Lock;
using Foundatio.Messaging;
using Foundatio.Queues;
using Foundatio.Resilience;
using Foundatio.Serializer;
using Foundatio.Xunit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Foundatio.Tests.Jobs;

public class WorkItemJobLeaseTests : TestWithLoggingBase
{
    public WorkItemJobLeaseTests(ITestOutputHelper output) : base(output) { }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessAsync_WhenAbandonmentObserverThrows_SkipsTerminalStatus(bool handlerReturnsFailure)
    {
        // Arrange
        using var cache = CreateCache();
        using var queue = CreateQueue(retries: 0);
        using var bus = CreateMessageBus();
        var locker = new CacheLockProvider(cache, bus, Log);
        await using var lease = await locker.AcquireAsync("item", cancellationToken: TestCancellationToken);
        var observerError = new InvalidOperationException("Abandonment observer failed");
        using var observer = queue.Abandoned.AddHandler((_, _) => throw observerError);
        var statuses = new ConcurrentQueue<int>();
        await bus.SubscribeAsync<WorkItemStatus>(s => statuses.Enqueue(s.Progress), TestCancellationToken);
        var handlers = new WorkItemHandlers();
        Exception? reportError = null;
        handlers.Register<string>(new RenewingWorkItemHandler(lease, async ctx =>
        {
            if (handlerReturnsFailure)
            {
                ctx.Result = JobResult.FailedWithMessage("Handler rejected the item");
                return;
            }

            await locker.ReleaseAsync(lease.Resource);
            reportError = await Record.ExceptionAsync(() => ctx.ReportProgressAsync(50));
        }, _logger));

        await queue.EnqueueAsync("item", true);
        var job = new WorkItemJob(queue, bus, handlers, Log);

        // Act
        var error = handlerReturnsFailure
            ? (await job.RunAsync(TestCancellationToken)).Error
            : await Assert.ThrowsAsync<InvalidOperationException>(() => job.RunAsync(TestCancellationToken));

        // Assert
        Assert.Same(observerError, error);
        if (!handlerReturnsFailure)
            Assert.IsType<LockException>(reportError);

        Assert.Equal([0], statuses);
        Assert.Equal(1, (await queue.GetQueueStatsAsync()).Abandoned);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessAsync_WhenAbandonmentOutcomeIsUnknown_SkipsFailureStatus(bool markedAbandoned)
    {
        // Arrange
        using var queue = CreateQueue(retries: 0);
        using var bus = CreateMessageBus();
        await queue.EnqueueAsync("item", true);
        var original = await queue.DequeueAsync(TestCancellationToken);
        Assert.NotNull(original);
        var error = new InvalidOperationException("Provider could not confirm abandonment");
        var entry = new Mock<IQueueEntry<WorkItemData>>();
        entry.SetupGet(e => e.Value).Returns(original.Value);
        entry.SetupGet(e => e.Id).Returns(original.Id);
        entry.SetupGet(e => e.IsAbandoned).Returns(() => original.IsAbandoned);
        entry.Setup(e => e.AbandonAsync()).Returns(() =>
        {
            if (markedAbandoned)
                original.MarkAbandoned();

            return Task.FromException(error);
        });

        var statuses = new ConcurrentQueue<int>();
        await bus.SubscribeAsync<WorkItemStatus>(s => statuses.Enqueue(s.Progress), TestCancellationToken);
        var handlers = new WorkItemHandlers();
        handlers.Register<string>(ctx =>
        {
            ctx.Result = JobResult.FailedWithMessage("Handler rejected the item");
            return Task.CompletedTask;
        });

        // Act
        var result = await new WorkItemJob(queue, bus, handlers, Log).ProcessAsync(entry.Object, TestCancellationToken);

        // Assert
        Assert.Same(error, result.Error);
        entry.Verify(e => e.AbandonAsync(), Times.Once);
        Assert.Equal([0], statuses);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessAsync_WhenAutoRenewedWorkItemLockIsLost_CancelsBeforeQueueRenewalCompletes(bool queueRenewalFails)
    {
        // Arrange
        using var cache = CreateCache();
        using var messageBus = CreateMessageBus();
        using var queue = CreateQueue(retries: 0, retryDelay: TimeSpan.Zero);
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
        using var cache = CreateCache();
        using var queue = CreateQueue(retries: 0, retryDelay: TimeSpan.Zero);
        using var messageBus = CreateMessageBus();
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessAsync_WhenCompetingAbandonmentWins_PreservesRetryStatus(bool handlerThrows)
    {
        // Arrange
        var clock = new FakeTimeProvider();
        using var queue = CreateMaintenanceQueue(clock, pauseFirstAbandonment: true);
        using var bus = CreateMessageBus();
        var statuses = new ConcurrentQueue<int>();
        await bus.SubscribeAsync<WorkItemStatus>(status => statuses.Enqueue(status.Progress), TestCancellationToken);
        var handlers = new WorkItemHandlers();
        int attempts = 0;
        handlers.Register<string>(ctx =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                if (handlerThrows)
                    throw new InvalidOperationException("Handler failed");

                ctx.Result = JobResult.FailedWithMessage("Handler rejected the item");
            }

            return Task.CompletedTask;
        });

        var job = new WorkItemJob(queue, bus, handlers, Log);
        await queue.EnqueueAsync("item", true);

        // Act
        var first = job.RunAsync(TestCancellationToken);
        JobResult retry;
        try
        {
            await queue.Abandoning.Task.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);
            await queue.ExpireLeaseAsync(clock);
            retry = await job.RunAsync(TestCancellationToken);
        }
        finally
        {
            queue.Resume.TrySetResult();
        }

        var error = handlerThrows
            ? await Record.ExceptionAsync(async () => { await first; })
            : (await first).Error;

        // Assert
        Assert.True(retry.IsSuccess);
        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal([0, 0, 100], statuses);
        var stats = await queue.GetQueueStatsAsync();
        Assert.Equal(1, stats.Timeouts);
        Assert.Equal(1, stats.Abandoned);
        Assert.Equal(1, stats.Completed);
    }

    [Fact]
    public async Task ProcessAsync_WhenCompletionObserverThrows_SkipsTerminalStatus()
    {
        // Arrange
        using var queue = CreateQueue();
        using var bus = CreateMessageBus();
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
        Assert.Equal([0], statuses);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ProcessAsync_WhenEntryIsRedelivered_PreservesRetryOutcome(bool renewOnProgress, bool redeliverDuringRenewal)
    {
        // Arrange
        using var queue = CreateQueue(retryDelay: TimeSpan.Zero);
        using var bus = CreateMessageBus();
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
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ProcessAsync_WhenExplicitRenewalFailureIsCaught_AbandonsWithProviderError(bool callbackThrows, bool otherOperationCancelled)
    {
        // Arrange
        using var cache = CreateCache();
        using var queue = CreateQueue(retries: 0);
        using var bus = CreateMessageBus();
        var locker = new CacheLockProvider(cache, bus, Log);
        await using var lease = await locker.AcquireAsync("item", cancellationToken: TestCancellationToken);
        LockException? renewalError = null;
        var handlers = new WorkItemHandlers();
        using var listener = ListenToJobActivities();
        Activity? activity = null;
        var cancellation = new OperationCanceledException("Other operation was cancelled", new CancellationToken(true));
        handlers.Register<string>(new RenewingWorkItemHandler(lease, async ctx =>
        {
            activity = Activity.Current;
            using var registration = ctx.CancellationToken.Register(() =>
            {
                if (callbackThrows)
                    throw new InvalidOperationException("Cancellation observer failed");
            });

            await locker.ReleaseAsync(lease.Resource);
            renewalError = await Assert.ThrowsAsync<LockException>(() => ctx.RenewLockAsync());
            Assert.True(ctx.CancellationToken.IsCancellationRequested);
            if (otherOperationCancelled)
                throw cancellation;
        }, _logger)
        { AutoRenewLockOnProgress = false });
        await queue.EnqueueAsync("item");

        // Act
        var result = await new WorkItemJob(queue, bus, handlers, Log).RunAsync(TestCancellationToken);

        // Assert
        Assert.NotNull(renewalError);
        Assert.Same(renewalError, result.Error);
        Assert.NotNull(activity);
        Assert.Equal(renewalError.Message, activity.StatusDescription);
        var messages = activity.Events.SelectMany(e => e.Tags)
            .Where(tag => String.Equals(tag.Key, "exception.message", StringComparison.Ordinal))
            .Select(tag => (string?)tag.Value);
        Assert.Equal(otherOperationCancelled, messages.Contains(cancellation.Message, StringComparer.Ordinal));
        Assert.Equal(callbackThrows, messages.Any(message => message?.Contains("Cancellation observer failed", StringComparison.Ordinal) == true));
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
        using var queue = CreateQueue(retries: 0, retryDelay: TimeSpan.Zero);
        await queue.EnqueueAsync(new MyWorkItem { SomeData = "Test" }, true);
        var entry = await queue.DequeueAsync(TestCancellationToken);
        Assert.NotNull(entry);

        var statuses = new ConcurrentQueue<WorkItemStatus>();
        using var messageBus = CreateMessageBus();
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
        using var cache = CreateCache();
        using var queue = CreateQueue(retries: 0, retryDelay: TimeSpan.Zero);
        using var messageBus = CreateMessageBus();
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
        using var cache = CreateCache();
        using var queue = CreateQueue();
        using var messageBus = CreateMessageBus();
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessAsync_WhenReleaseFails_ReportsSettledOutcome(bool renewalFails)
    {
        // Arrange
        using var cache = CreateCache();
        using var lockBus = CreateMessageBus();
        using var statusBus = CreateMessageBus();
        using var queue = CreateQueue(retries: 0);
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
        using var queue = CreateQueue(retries: 0);
        using var bus = CreateMessageBus();
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
        using var cache = CreateCache();
        using var queue = CreateQueue(retries: 0);
        using var bus = CreateMessageBus();
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

    [Fact]
    public async Task ReportProgressAsync_WhenAbandonmentBecomesVisibleAtEntryCheck_StopsHandler()
    {
        // Arrange
        using var queue = CreateQueue();
        using var bus = CreateMessageBus();
        await queue.EnqueueAsync("item");
        var original = await queue.DequeueAsync(TestCancellationToken);
        Assert.NotNull(original);
        var entry = new Mock<IQueueEntry<WorkItemData>>();
        entry.SetupGet(e => e.Id).Returns(original.Id);
        entry.SetupGet(e => e.Value).Returns(original.Value);
        entry.SetupGet(e => e.IsAbandoned).Returns(() =>
        {
            bool observed = original.IsAbandoned;
            original.MarkAbandoned();
            return observed;
        });

        bool continued = false;
        var handlers = new WorkItemHandlers();
        handlers.Register<string>(new DelegateWorkItemHandler(async ctx =>
        {
            await ctx.ReportProgressAsync(50);
            continued = true;
        }, _logger)
        { AutoRenewLockOnProgress = true });

        // Act
        var result = await new WorkItemJob(queue, bus, handlers, Log).ProcessAsync(entry.Object, TestCancellationToken);

        // Assert
        Assert.False(continued);
        Assert.IsType<InvalidOperationException>(result.Error);
    }

    [Fact]
    public async Task ReportProgressAsync_WhenEntryExpiresDuringDelivery_PreservesRetryWrite()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        using var queue = CreateMaintenanceQueue(clock);
        using var cache = CreateCache(clock);
        using var bus = CreateMessageBus();
        var locker = new CacheLockProvider(cache, bus, Log);
        await using var lease = await locker.AcquireAsync("item", cancellationToken: TestCancellationToken);
        var handlers = new WorkItemHandlers();
        handlers.Register<string>(new RenewingWorkItemHandler(lease, async ctx =>
        {
            await ctx.ReportProgressAsync(50);
            ctx.CancellationToken.ThrowIfCancellationRequested();
            await cache.SetAsync("saved", "original worker");
        }, _logger));

        var statuses = new ConcurrentQueue<int>();
        JobResult? retry = null;
        await bus.SubscribeAsync<WorkItemStatus>(async status =>
        {
            statuses.Enqueue(status.Progress);
            if (status.Progress != 50)
                return;

            await queue.ExpireLeaseAsync(clock);
            await using var retryLease = await locker.AcquireAsync("item", cancellationToken: TestCancellationToken);
            var retryHandlers = new WorkItemHandlers();
            retryHandlers.Register<string>(new RenewingWorkItemHandler(retryLease, async _ => { await cache.SetAsync("saved", "retry"); }, _logger));
            retry = await new WorkItemJob(queue, bus, retryHandlers, Log).RunAsync(TestCancellationToken);
        }, TestCancellationToken);

        await queue.EnqueueAsync("item", true);

        // Act
        var result = await new WorkItemJob(queue, bus, handlers, Log).RunAsync(TestCancellationToken);

        // Assert
        Assert.True(retry?.IsSuccess);
        Assert.Equal("retry", (await cache.GetAsync<string>("saved")).Value);
        Assert.IsType<InvalidOperationException>(result.Error);
        Assert.Equal([0, 50, 0, 100], statuses);
        Assert.Equal(1, (await queue.GetQueueStatsAsync()).Timeouts);
    }

    [Fact]
    public async Task ReportProgressAsync_WhenWorkItemLockFailsDuringDelivery_ThrowsOriginalError()
    {
        // Arrange
        using var queue = CreateQueue(retries: 0);
        using var cache = CreateCache();
        using var bus = CreateMessageBus();
        var locker = new CacheLockProvider(cache, bus, Log);
        await using var lease = await locker.AcquireAsync("item", cancellationToken: TestCancellationToken);
        WorkItemContext? context = null;
        Exception? renewalError = null;
        Exception? reportError = null;
        var statuses = new ConcurrentQueue<int>();
        await bus.SubscribeAsync<WorkItemStatus>(async status =>
        {
            statuses.Enqueue(status.Progress);
            if (status.Progress == 50)
            {
                await locker.ReleaseAsync(lease.Resource);
                renewalError = await Record.ExceptionAsync(() => context!.RenewLockAsync());
            }
        }, TestCancellationToken);

        var handlers = new WorkItemHandlers();
        handlers.Register<string>(new RenewingWorkItemHandler(lease, async ctx =>
        {
            context = ctx;
            reportError = await Record.ExceptionAsync(() => ctx.ReportProgressAsync(50));
        }, _logger));

        await queue.EnqueueAsync("item", true);

        // Act
        var result = await new WorkItemJob(queue, bus, handlers, Log).RunAsync(TestCancellationToken);

        // Assert
        Assert.IsType<LockException>(renewalError);
        Assert.Same(renewalError, reportError);
        Assert.Same(renewalError, result.Error);
        Assert.Equal([0, 50, -1], statuses);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RunAsync_WhenAutoRenewedWorkItemLockIsHeld_CompletesEntry(bool queueRenewalFails, bool sendProgressReports)
    {
        // Arrange
        using var queue = CreateQueue();
        using var cache = CreateCache();
        using var bus = CreateMessageBus();
        var locker = new CacheLockProvider(cache, bus, Log);
        await using var lease = await locker.AcquireAsync("work-item", cancellationToken: TestCancellationToken);
        var handlers = new WorkItemHandlers();
        handlers.Register<MyWorkItem>(new RenewingWorkItemHandler(lease, async context =>
        {
            using var observer = queueRenewalFails
                ? queue.LockRenewed.AddHandler((_, _) => throw new InvalidOperationException("Queue renewal failed"))
                : null;
            await context.ReportProgressAsync(50);
            context.CancellationToken.ThrowIfCancellationRequested();
        }, _logger));

        var job = new WorkItemJob(queue, bus, handlers, Log);
        IQueueEntry<WorkItemData>? entry = null;
        using var dequeued = queue.Dequeued.AddSyncHandler((_, args) => entry = args.Entry);
        await queue.EnqueueAsync(new MyWorkItem { SomeData = "Test" }, sendProgressReports);

        // Act
        var result = await job.RunAsync(TestCancellationToken);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(1, lease.RenewalCount);
        Assert.NotNull(entry);
        Assert.True(entry.IsCompleted);
        Assert.False(entry.IsAbandoned);
        Assert.Equal(1, (await queue.GetQueueStatsAsync()).Completed);
    }

    private static ActivityListener ListenToJobActivities()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => ReferenceEquals(source, FoundatioDiagnostics.ActivitySource),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private TOptions CreateOptions<TOptions>() where TOptions : SharedOptions, new() => new()
    {
        LoggerFactory = Log,
        Serializer = DefaultSerializer.Instance,
        ResiliencePolicyProvider = DefaultResiliencePolicyProvider.Instance
    };

    private InMemoryCacheClient CreateCache(TimeProvider? clock = null)
    {
        var options = CreateOptions<InMemoryCacheClientOptions>();
        options.TimeProvider = clock ?? options.TimeProvider;
        return new InMemoryCacheClient(options);
    }

    private InMemoryMessageBus CreateMessageBus() => new(CreateOptions<InMemoryMessageBusOptions>());

    private InMemoryQueue<WorkItemData> CreateQueue(int? retries = null, TimeSpan? retryDelay = null)
    {
        var options = CreateOptions<InMemoryQueueOptions<WorkItemData>>();
        options.Retries = retries ?? options.Retries;
        options.RetryDelay = retryDelay ?? options.RetryDelay;
        return new InMemoryQueue<WorkItemData>(options);
    }

    private MaintenanceQueue CreateMaintenanceQueue(TimeProvider clock, bool pauseFirstAbandonment = false)
    {
        var options = CreateOptions<InMemoryQueueOptions<WorkItemData>>();
        options.TimeProvider = clock;
        options.RetryDelay = TimeSpan.Zero;
        options.WorkItemTimeout = TimeSpan.FromHours(24);
        return new MaintenanceQueue(options, pauseFirstAbandonment);
    }

    private sealed class MaintenanceQueue(InMemoryQueueOptions<WorkItemData> options, bool pauseFirstAbandonment) : InMemoryQueue<WorkItemData>(options)
    {
        private int _abandonments;
        public TaskCompletionSource Abandoning { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ExpireLeaseAsync(FakeTimeProvider clock)
        {
            clock.AdjustTime(clock.GetUtcNow().Add(_options.WorkItemTimeout).AddSeconds(1));
            return DoMaintenanceAsync();
        }

        public override async Task AbandonAsync(IQueueEntry<WorkItemData> entry)
        {
            if (pauseFirstAbandonment && Interlocked.Increment(ref _abandonments) == 1)
            {
                Abandoning.TrySetResult();
                await Resume.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }

            await base.AbandonAsync(entry);
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

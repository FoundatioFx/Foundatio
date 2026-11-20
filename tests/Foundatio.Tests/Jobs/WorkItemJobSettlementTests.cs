using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Lock;
using Foundatio.Messaging;
using Foundatio.Queues;
using Foundatio.Resilience;
using Foundatio.Serializer;
using Foundatio.Xunit;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Foundatio.Tests.Jobs;

public class WorkItemJobSettlementTests : TestWithLoggingBase
{
    public WorkItemJobSettlementTests(ITestOutputHelper output) : base(output) { }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessAsync_WhenAbandonedEntryHasOutlivingWork_ReleasesEntry(bool captureObserverContext)
    {
        // Arrange
        using var queue = CreateQueue(retries: captureObserverContext ? 0 : 1, timeProvider: new FakeTimeProvider());
        using var bus = CreateBus();
        ExecutionContext? observerContext = null;
        using var observer = captureObserverContext
            ? queue.Abandoned.AddSyncHandler((_, _) => observerContext = ExecutionContext.Capture())
            : null;

        // Act
        var entry = await AbandonAndForgetEntryAsync(queue, bus);
        if (captureObserverContext)
            await queue.DeleteQueueAsync();

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        // Assert
        if (captureObserverContext)
            Assert.NotNull(observerContext);

        Assert.False(entry.TryGetTarget(out _));
        GC.KeepAlive(observerContext);
        GC.KeepAlive(queue);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(1, 25)]
    public async Task ProcessAsync_WhenAbandonmentObserverThrows_PreservesDeadLetterOrRetry(int retries, int retryDelayMs)
    {
        // Arrange
        var error = new InvalidOperationException("Abandonment observer failed");
        using var queue = CreateQueue(retries: retries, retryDelay: TimeSpan.FromMilliseconds(retryDelayMs));
        using var observer = queue.Abandoned.AddHandler((_, _) => throw error);
        using var bus = CreateBus();
        var statuses = new ConcurrentQueue<int>();
        await bus.SubscribeAsync<WorkItemStatus>(s => statuses.Enqueue(s.Progress), TestCancellationToken);
        await queue.EnqueueAsync("item", true);
        var entry = await queue.DequeueAsync(TestCancellationToken);
        Assert.NotNull(entry);

        // Act
        var result = await new WorkItemJob(queue, bus, RejectItem(), Log).ProcessAsync(entry, TestCancellationToken);

        // Assert
        var stats = await queue.GetQueueStatsAsync();
        Assert.Same(error, result.Error);
        Assert.Equal(1, stats.Abandoned);
        Assert.Equal(1 - retries, stats.Deadletter);
        Assert.Equal([0], statuses);
        if (retries > 0)
        {
            var retry = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(retry);
            Assert.Equal(entry.Id, retry.Id);
            Assert.Equal(2, retry.Attempts);
            await retry.CompleteAsync();
        }

        Assert.Equal(0, (await queue.GetQueueStatsAsync()).Queued);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessAsync_WhenAnotherCallerCompletesEntry_SkipsTerminalStatus(bool handlerFails)
    {
        // Arrange
        using var queue = CreateQueue();
        using var bus = CreateBus();
        var statuses = new ConcurrentQueue<int>();
        await bus.SubscribeAsync<WorkItemStatus>(s => statuses.Enqueue(s.Progress), TestCancellationToken);
        await queue.EnqueueAsync("item", true);
        var entry = await queue.DequeueAsync(TestCancellationToken);
        Assert.NotNull(entry);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlers = new WorkItemHandlers();
        handlers.Register<string>(async ctx =>
        {
            started.SetResult();
            await finish.Task.WaitAsync(TestCancellationToken);
            if (handlerFails)
                ctx.Result = JobResult.FailedWithMessage("rejected");
        });

        // Act
        var processing = new WorkItemJob(queue, bus, handlers, Log).ProcessAsync(entry, TestCancellationToken);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);
            await entry.CompleteAsync();
        }
        finally
        {
            finish.TrySetResult();
        }

        var result = await processing;

        // Assert
        Assert.Equal(!handlerFails, result.IsSuccess);
        Assert.True(entry.IsCompleted);
        Assert.Equal([0], statuses);
    }

    [Fact]
    public async Task ProcessAsync_WhenCompletionFailsAndAbandonmentSucceeds_ReportsAbandonment()
    {
        // Arrange
        using var queue = CreateQueue(Failure.CompletionBeforeCommit);
        using var bus = CreateBus();
        var statuses = new ConcurrentQueue<int>();
        await bus.SubscribeAsync<WorkItemStatus>(s => statuses.Enqueue(s.Progress), TestCancellationToken);
        var handlers = new WorkItemHandlers();
        handlers.Register<string>(_ => Task.CompletedTask);
        await queue.EnqueueAsync("item", true);

        // Act
        var result = await new WorkItemJob(queue, bus, handlers, Log).RunAsync(TestCancellationToken);

        // Assert
        Assert.Same(queue.ObserverError, result.Error);
        var stats = await queue.GetQueueStatsAsync();
        Assert.Equal(0, stats.Completed);
        Assert.Equal(1, stats.Deadletter);
        Assert.Equal([0, -1], statuses);
    }

    [Fact]
    public async Task ProcessAsync_WhenDifferentDeliveryHasIdenticalIdentifiers_SkipsFailureStatus()
    {
        // Arrange
        using var queue = CreateQueue(Failure.DifferentDelivery);
        using var bus = CreateBus();
        var statuses = new ConcurrentQueue<int>();
        await bus.SubscribeAsync<WorkItemStatus>(s => statuses.Enqueue(s.Progress), TestCancellationToken);
        await queue.EnqueueAsync("item", true);
        var entry = await queue.DequeueAsync(TestCancellationToken);
        Assert.NotNull(entry);

        // Act
        var result = await new WorkItemJob(queue, bus, RejectItem(), Log).ProcessAsync(new ForwardingEntry(entry), TestCancellationToken);

        // Assert
        Assert.Same(queue.ObserverError, result.Error);
        Assert.False(entry.IsAbandoned);
        Assert.Equal([0], statuses);
    }

    [Fact]
    public async Task ProcessAsync_WhenNestedAbandonmentWins_SkipsFailureForOuterCall()
    {
        // Arrange
        using var queue = CreateQueue(Failure.CompetingCall);
        using var bus = CreateBus();
        var statuses = new ConcurrentQueue<int>();
        await bus.SubscribeAsync<WorkItemStatus>(s => statuses.Enqueue(s.Progress), TestCancellationToken);
        await queue.EnqueueAsync("item", true);

        // Act
        var result = await new WorkItemJob(queue, bus, RejectItem(), Log).RunAsync(TestCancellationToken);

        // Assert
        Assert.Same(queue.ObserverError, result.Error);
        Assert.Equal(1, (await queue.GetQueueStatsAsync()).Deadletter);
        Assert.Equal([0], statuses);
    }

    [Theory]
    [InlineData(Failure.ForwardedEntry, false)]
    [InlineData(Failure.ForwardedProviderEntry, false)]
    [InlineData(Failure.BothForward, false)]
    [InlineData(Failure.BothForward, true)]
    [InlineData(Failure.WrappedObserver, false)]
    [InlineData(Failure.WrappedObserver, true)]
    [InlineData(Failure.Metrics, false)]
    [InlineData(Failure.Metrics, true)]
    [InlineData(Failure.AfterNotification, false)]
    [InlineData(Failure.AfterNotification, true)]
    [InlineData(Failure.PrivateMetadata, false)]
    [InlineData(Failure.AsyncPrivateMetadata, false)]
    [InlineData(Failure.AsyncPrivateMetadata, true)]
    [InlineData(Failure.BeforeBaseNotification, false)]
    [InlineData(Failure.BeforeBaseNotification, true)]
    public async Task ProcessAsync_WhenProviderFailsAfterSettling_SkipsTerminalStatus(Failure failure, bool completing)
    {
        // Arrange
        string prefix = Guid.NewGuid().ToString("N");
        using var queue = CreateQueue(failure, prefix);
        using var bus = CreateBus();
        var statuses = new ConcurrentQueue<int>();
        await bus.SubscribeAsync<WorkItemStatus>(s => statuses.Enqueue(s.Progress), TestCancellationToken);
        bool observerThrows = failure is not (Failure.Metrics or Failure.AfterNotification);
        using var abandoned = observerThrows ? queue.Abandoned.AddSyncHandler((_, _) => throw queue.ObserverError) : null;
        using var completed = observerThrows ? queue.Completed.AddSyncHandler((_, _) => throw queue.ObserverError) : null;
        using var listener = new MeterListener();
        if (failure == Failure.Metrics)
        {
            string instrumentName = $"foundatio.workitemdata.{prefix}.{(completing ? "completed" : "abandoned")}";
            listener.InstrumentPublished = (instrument, meterListener) =>
            {
                if (ReferenceEquals(instrument.Meter, FoundatioDiagnostics.Meter) && String.Equals(instrument.Name, instrumentName, StringComparison.Ordinal))
                    meterListener.EnableMeasurementEvents(instrument);
            };

            listener.SetMeasurementEventCallback<long>((_, _, _, _) => throw queue.ObserverError);
            listener.Start();
        }

        await queue.EnqueueAsync("item", true);
        var entry = await queue.DequeueAsync(TestCancellationToken);
        Assert.NotNull(entry);
        var delivered = failure switch
        {
            Failure.ForwardedEntry or Failure.BothForward => new ForwardingEntry(entry),
            Failure.PrivateMetadata or Failure.AsyncPrivateMetadata => new ForwardingEntry(entry, copyProperties: true, traceSettlement: failure == Failure.AsyncPrivateMetadata),
            _ => entry
        };

        var handlers = new WorkItemHandlers();
        handlers.Register<string>(ctx =>
        {
            ctx.Result = completing ? JobResult.Success : JobResult.FailedWithMessage("rejected");
            return Task.CompletedTask;
        });

        // Act
        var result = await new WorkItemJob(queue, bus, handlers, Log).ProcessAsync(delivered, TestCancellationToken);

        // Assert
        Assert.Same(failure == Failure.WrappedObserver ? queue.ProviderError : queue.ObserverError, result.Error);
        Assert.Equal(completing, entry.IsCompleted);
        Assert.Equal(!completing, entry.IsAbandoned);
        var stats = await queue.GetQueueStatsAsync();
        Assert.Equal(1, completing ? stats.Completed : stats.Deadletter);
        Assert.Equal([0], statuses);
    }

    [Fact]
    public async Task ProcessAsync_WhenProviderOnlyRaisesNotification_SkipsTerminalStatus()
    {
        // Arrange
        using var queue = CreateQueue(Failure.BeforeCommit);
        using var bus = CreateBus();
        var statuses = new ConcurrentQueue<int>();
        await bus.SubscribeAsync<WorkItemStatus>(s => statuses.Enqueue(s.Progress), TestCancellationToken);
        await queue.EnqueueAsync("item", true);

        // Act
        var result = await new WorkItemJob(queue, bus, RejectItem(), Log).RunAsync(TestCancellationToken);

        // Assert
        Assert.Same(queue.ObserverError, result.Error);
        Assert.Equal(1, (await queue.GetQueueStatsAsync()).Working);
        Assert.Equal([0], statuses);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessAsync_WhenReleaseFailsAfterHandlerError_PropagatesReleaseFailure(bool alreadyCompleted)
    {
        // Arrange
        using var queue = CreateQueue();
        using var bus = CreateBus();
        var handlerError = new InvalidOperationException("Handler failed");
        var releaseError = new InvalidOperationException("Lock release failed");
        var lease = new Mock<ILock>();
        lease.Setup(l => l.ReleaseAsync()).ThrowsAsync(releaseError);
        await queue.EnqueueAsync("item");
        var entry = await queue.DequeueAsync(TestCancellationToken);
        Assert.NotNull(entry);
        var handlers = new WorkItemHandlers();
        handlers.Register<string>(new RenewingWorkItemHandler(lease.Object, async _ =>
        {
            if (alreadyCompleted)
                await entry.CompleteAsync();

            throw handlerError;
        }, _logger));

        var job = new WorkItemJob(queue, bus, handlers, Log);

        // Act
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => job.ProcessAsync(entry, TestCancellationToken));

        // Assert
        Assert.Same(releaseError, error);
        Assert.Equal(alreadyCompleted, entry.IsCompleted);
        Assert.Equal(!alreadyCompleted, entry.IsAbandoned);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessAsync_WhenTerminalProgressDeliveryFails_PreservesProcessingOutcome(bool releaseFails)
    {
        // Arrange
        using var queue = CreateQueue();
        using var bus = CreateBus();
        var releaseError = new InvalidOperationException("Lock release failed");
        var lease = new Mock<ILock>();
        lease.Setup(l => l.ReleaseAsync()).Returns(() =>
        {
            bus.Dispose();
            return releaseFails ? Task.FromException(releaseError) : Task.CompletedTask;
        });

        var handlers = new WorkItemHandlers();
        handlers.Register<string>(new RenewingWorkItemHandler(lease.Object, _ => Task.CompletedTask, _logger));
        await queue.EnqueueAsync("item", true);
        var entry = await queue.DequeueAsync(TestCancellationToken);
        Assert.NotNull(entry);
        var job = new WorkItemJob(queue, bus, handlers, Log);

        // Act & Assert
        if (releaseFails)
            Assert.Same(releaseError, await Assert.ThrowsAsync<InvalidOperationException>(() => job.ProcessAsync(entry, TestCancellationToken)));
        else
            Assert.True((await job.ProcessAsync(entry, TestCancellationToken)).IsSuccess);

        Assert.True(entry.IsCompleted);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<WeakReference<IQueueEntry<WorkItemData>>> AbandonAndForgetEntryAsync(SettlementQueue queue, InMemoryMessageBus bus)
    {
        await queue.EnqueueAsync("item", true);
        var entry = await queue.DequeueAsync(TestCancellationToken);
        Assert.NotNull(entry);
        var weak = new WeakReference<IQueueEntry<WorkItemData>>(entry);
        await new WorkItemJob(queue, bus, RejectItem(), Log).ProcessAsync(entry, TestCancellationToken);
        return weak;
    }

    private InMemoryMessageBus CreateBus() => new(o => o.LoggerFactory(Log).Serializer(DefaultSerializer.Instance).ResiliencePolicyProvider(DefaultResiliencePolicyProvider.Instance));

    private SettlementQueue CreateQueue(Failure? failure = null, string? prefix = null, int retries = 0, TimeSpan? retryDelay = null, TimeProvider? timeProvider = null)
    {
        var options = new InMemoryQueueOptions<WorkItemData>
        {
            LoggerFactory = Log,
            Serializer = DefaultSerializer.Instance,
            ResiliencePolicyProvider = DefaultResiliencePolicyProvider.Instance,
            MetricsPrefix = prefix,
            Retries = retries,
            TimeProvider = timeProvider ?? TimeProvider.System
        };

        if (retryDelay.HasValue)
            options.RetryDelay = retryDelay.Value;

        return new SettlementQueue(options, failure);
    }

    private static WorkItemHandlers RejectItem()
    {
        var handlers = new WorkItemHandlers();
        handlers.Register<string>(ctx =>
        {
            ctx.Result = JobResult.FailedWithMessage("rejected");
            return Task.CompletedTask;
        });

        return handlers;
    }

    public enum Failure
    {
        AfterNotification,
        AsyncPrivateMetadata,
        BeforeBaseNotification,
        BeforeCommit,
        BothForward,
        CompetingCall,
        CompletionBeforeCommit,
        DifferentDelivery,
        ForwardedEntry,
        ForwardedProviderEntry,
        Metrics,
        PrivateMetadata,
        WrappedObserver
    }

    private sealed class SettlementQueue(InMemoryQueueOptions<WorkItemData> options, Failure? failure) : InMemoryQueue<WorkItemData>(options)
    {
        private int _calls;
        public Exception ObserverError { get; } = new InvalidOperationException("Observer failed");
        public Exception? ProviderError { get; private set; }

        public override async Task AbandonAsync(IQueueEntry<WorkItemData> entry)
        {
            if (failure == Failure.DifferentDelivery)
            {
                IQueueEntry<WorkItemData> other = new QueueEntry<WorkItemData>(entry.Id, entry.CorrelationId, entry.Value, this, DateTime.UtcNow, entry.Attempts);
                other.MarkAbandoned();
                await OnAbandonedAsync(new ForwardingEntry(other));
                throw ObserverError;
            }

            if (failure == Failure.CompetingCall && Interlocked.Increment(ref _calls) == 1)
            {
                await Task.Run(entry.AbandonAsync);
                throw ObserverError;
            }

            if (failure == Failure.BeforeCommit)
            {
                await OnAbandonedAsync(entry);
                throw ObserverError;
            }

            await SettleWithFailureAsync(entry, completing: false);
        }

        public override Task CompleteAsync(IQueueEntry<WorkItemData> entry) =>
            failure == Failure.CompletionBeforeCommit ? Task.FromException(ObserverError) : SettleWithFailureAsync(entry, completing: true);

        protected override Task OnAbandonedAsync(IQueueEntry<WorkItemData> entry) =>
            failure == Failure.BeforeBaseNotification ? Task.FromException(ObserverError) : base.OnAbandonedAsync(entry);

        protected override Task OnCompletedAsync(IQueueEntry<WorkItemData> entry) =>
            failure == Failure.BeforeBaseNotification ? Task.FromException(ObserverError) : base.OnCompletedAsync(entry);

        private async Task SettleWithFailureAsync(IQueueEntry<WorkItemData> entry, bool completing)
        {
            var delivered = failure is Failure.ForwardedProviderEntry or Failure.BothForward ? new ForwardingEntry(entry) : entry;
            try
            {
                if (completing)
                    await base.CompleteAsync(delivered);
                else
                    await base.AbandonAsync(delivered);
            }
            catch (Exception ex) when (failure == Failure.WrappedObserver)
            {
                ProviderError = new QueueException("Provider failed", ex);
                throw ProviderError;
            }

            if (failure == Failure.AfterNotification)
                throw ObserverError;
        }
    }

    private sealed class ForwardingEntry(IQueueEntry<WorkItemData> entry, bool copyProperties = false, bool traceSettlement = false) : IQueueEntry<WorkItemData>
    {
        public string Id => entry.Id;
        public string? CorrelationId => entry.CorrelationId;
        public string? GroupId => entry.GroupId;
        public IDictionary<string, string> Properties { get; } = copyProperties ? new Dictionary<string, string>(entry.Properties) : entry.Properties;
        public Type? EntryType => entry.EntryType;
        public bool IsAbandoned => entry.IsAbandoned;
        public bool IsCompleted => entry.IsCompleted;
        public int Attempts => entry.Attempts;
        public WorkItemData Value => entry.Value;
        public object GetValue() => entry.GetValue();
        public void MarkAbandoned() => entry.MarkAbandoned();
        public void MarkCompleted() => entry.MarkCompleted();
        public Task AbandonAsync() => traceSettlement ? TraceSettlementAsync(entry.AbandonAsync) : entry.AbandonAsync();
        public Task CompleteAsync() => traceSettlement ? TraceSettlementAsync(entry.CompleteAsync) : entry.CompleteAsync();
        public Task RenewLockAsync() => entry.RenewLockAsync();
        public ValueTask DisposeAsync() => entry.DisposeAsync();

        private static async Task TraceSettlementAsync(Func<Task> settleAsync)
        {
            using var activity = new Activity("Settle queue entry").Start();
            await settleAsync();
        }
    }
}

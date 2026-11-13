using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Foundatio.Queues;
using Foundatio.Resilience;
using Foundatio.Xunit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Foundatio.Tests.Queue;

public class QueueWorkerTests : TestWithLoggingBase
{
    public QueueWorkerTests(ITestOutputHelper output) : base(output) { }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task StartWorker_WhenHandlerAlreadySettledEntry_DoesNotReenterOpenCircuitAsync(bool abandon, bool cancelHandler)
    {
        using var timeout = CreateTimeout();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var circuitBreaker = new CircuitBreaker();
        using var queue = CreateQueue(circuitBreaker: circuitBreaker);
        await queue.EnqueueAsync(new SimpleWorkItem());

        await queue.StartWorkingAsync(async (entry, token) =>
        {
            if (abandon)
                await entry.AbandonAsync();
            else
                await entry.CompleteAsync();

            circuitBreaker.Open();
            await cancellation.CancelAsync();
            if (cancelHandler)
                token.ThrowIfCancellationRequested();
        }, autoComplete: true, cancellationToken: cancellation.Token);
        await queue.Worker.WaitAsync(timeout.Token);

        var stats = await queue.GetQueueStatsAsync();
        Assert.Equal(abandon ? 0 : 1, stats.Completed);
        Assert.Equal(abandon ? 1 : 0, stats.Abandoned);
        Assert.Equal(0, stats.Errors);
        Assert.Equal(abandon ? 0 : 1, queue.CompleteAttempts);
        Assert.Equal(abandon ? 1 : 0, queue.AbandonAttempts);
        Assert.DoesNotContain(Log.LogEntries, entry => entry.LogLevel == LogLevel.Error);
    }

    [Fact]
    public async Task StartWorker_WhenHandlerIsCancelled_AbandonsWithoutWorkerErrorAsync()
    {
        using var timeout = CreateTimeout();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        using var queue = CreateQueue();
        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await queue.EnqueueAsync(new SimpleWorkItem());

        await queue.StartWorkingAsync(async (_, token) =>
        {
            handlerEntered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, autoComplete: true, cancellationToken: cancellation.Token);
        await handlerEntered.Task.WaitAsync(timeout.Token);
        await cancellation.CancelAsync();
        await queue.Worker.WaitAsync(timeout.Token);

        var stats = await queue.GetQueueStatsAsync();
        Assert.Equal(1, stats.Abandoned);
        Assert.Equal(0, stats.Completed);
        Assert.Equal(0, stats.Working);
        Assert.Equal(0, stats.Errors);
        Assert.Equal(1, queue.AbandonAttempts);
        Assert.Equal(0, queue.CompleteAttempts);
    }

    [Fact]
    public async Task StartWorker_WhenSuccessfulHandlerCancelsWorker_CompletesEntryOnceAsync()
    {
        using var timeout = CreateTimeout();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        using var queue = CreateQueue();
        await queue.EnqueueAsync(new SimpleWorkItem());

        await queue.StartWorkingAsync((_, _) => cancellation.CancelAsync(), autoComplete: true, cancellationToken: cancellation.Token);
        await queue.Worker.WaitAsync(timeout.Token);

        var stats = await queue.GetQueueStatsAsync();
        Assert.Equal(1, stats.Completed);
        Assert.Equal(0, stats.Abandoned);
        Assert.Equal(0, stats.Working);
        Assert.Equal(0, stats.Errors);
        Assert.Equal(1, queue.CompleteAttempts);
    }

    [Fact]
    public async Task StartWorker_WhenAbandonFailsBeforeSettlement_DoesNotCompleteFailedEntryAndKeepsWorkingAsync()
    {
        using var timeout = CreateTimeout();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        using var queue = CreateQueue();
        queue.FailAbandon = _ => true;
        await queue.EnqueueAsync(new SimpleWorkItem { Data = "First" });
        await queue.EnqueueAsync(new SimpleWorkItem { Data = "Second" });

        await queue.StartWorkingAsync((entry, _) =>
        {
            if (entry.Value.Data == "First")
                throw new InvalidOperationException("Handler failed");

            return cancellation.CancelAsync();
        }, autoComplete: true, cancellationToken: cancellation.Token);
        await queue.Worker.WaitAsync(timeout.Token);

        var stats = await queue.GetQueueStatsAsync();
        Assert.Equal(1, stats.Completed);
        Assert.Equal(0, stats.Abandoned);
        Assert.Equal(1, stats.Working);
        Assert.Equal(1, stats.Errors);
        Assert.Equal(3, queue.AbandonAttempts);
        Assert.Equal(1, queue.CompleteAttempts);
        Assert.Equal("Second", Assert.Single(queue.GetCompletedEntries()).Value.Data);
        Assert.Equal("First", Assert.Single(queue.GetDequeuedEntries()).Value.Data);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task StartWorker_WhenAutoCompleteFails_RetriesAndKeepsWorkingAsync(int failures)
    {
        using var timeout = CreateTimeout();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        using var queue = CreateQueue();
        int firstEntryAttempts = 0;
        queue.FailComplete = entry => entry.Value.Data == "First" && ++firstEntryAttempts <= failures;
        await queue.EnqueueAsync(new SimpleWorkItem { Data = "First" });
        await queue.EnqueueAsync(new SimpleWorkItem { Data = "Second" });

        await queue.StartWorkingAsync((entry, _) => entry.Value.Data == "Second" ? cancellation.CancelAsync() : Task.CompletedTask,
            autoComplete: true, cancellationToken: cancellation.Token);
        await queue.Worker.WaitAsync(timeout.Token);

        bool exhausted = failures == 3;
        var stats = await queue.GetQueueStatsAsync();
        Assert.Equal(exhausted ? 1 : 2, stats.Completed);
        Assert.Equal(exhausted ? 1 : 0, stats.Working);
        Assert.Equal(exhausted ? 1 : 0, stats.Errors);
        Assert.Equal(0, stats.Abandoned);
        Assert.Equal(exhausted ? 3 : 2, firstEntryAttempts);
        Assert.Equal(firstEntryAttempts + 1, queue.CompleteAttempts);
        Assert.Contains(queue.GetCompletedEntries(), entry => entry.Value.Data == "Second");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task StartWorker_WhenSettlementFails_UsesConfiguredResiliencePolicyAsync(int maxAttempts)
    {
        using var timeout = CreateTimeout();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        using var queue = CreateQueue(maxAttempts: maxAttempts);
        queue.FailComplete = entry => entry.Value.Data == "First";
        await queue.EnqueueAsync(new SimpleWorkItem { Data = "First" });
        await queue.EnqueueAsync(new SimpleWorkItem { Data = "Second" });

        await queue.StartWorkingAsync((entry, _) => entry.Value.Data == "Second" ? cancellation.CancelAsync() : Task.CompletedTask,
            autoComplete: true, cancellationToken: cancellation.Token);
        await queue.Worker.WaitAsync(timeout.Token);

        var stats = await queue.GetQueueStatsAsync();
        Assert.Equal(maxAttempts + 1, queue.CompleteAttempts);
        Assert.Equal(1, stats.Errors);
        Assert.Equal(1, stats.Completed);
        Assert.Equal(1, stats.Working);
        Assert.Equal("Second", Assert.Single(queue.GetCompletedEntries()).Value.Data);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartWorker_WhenSettlementCallbackThrows_DoesNotRepeatSettlementAsync(bool abandon)
    {
        using var timeout = CreateTimeout();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        using var queue = CreateQueue();
        int failedCallbacks = 0;
        using var completedHandler = queue.Completed.AddSyncHandler((_, args) =>
        {
            if (!abandon && args.Entry.Value.Data == "First")
            {
                Interlocked.Increment(ref failedCallbacks);
                throw new InvalidOperationException("Completion callback failed");
            }
        });
        using var abandonedHandler = queue.Abandoned.AddSyncHandler((_, args) =>
        {
            if (abandon && args.Entry.Value.Data == "First")
            {
                Interlocked.Increment(ref failedCallbacks);
                throw new InvalidOperationException("Abandonment callback failed");
            }
        });
        await queue.EnqueueAsync(new SimpleWorkItem { Data = "First" });
        await queue.EnqueueAsync(new SimpleWorkItem { Data = "Second" });

        await queue.StartWorkingAsync((entry, _) =>
        {
            if (entry.Value.Data == "Second")
                return cancellation.CancelAsync();
            if (abandon)
                throw new InvalidOperationException("Handler failed");

            return Task.CompletedTask;
        }, autoComplete: true, cancellationToken: cancellation.Token);
        await queue.Worker.WaitAsync(timeout.Token);

        var stats = await queue.GetQueueStatsAsync();
        Assert.Equal(1, failedCallbacks);
        Assert.Equal(abandon ? 1 : 2, stats.Completed);
        Assert.Equal(abandon ? 1 : 0, stats.Abandoned);
        Assert.Equal(abandon ? 1 : 0, stats.Errors);
        Assert.Equal(0, stats.Working);
        Assert.Equal(abandon ? 1 : 2, queue.CompleteAttempts);
        Assert.Equal(abandon ? 1 : 0, queue.AbandonAttempts);
    }

    [Fact]
    public async Task StartWorker_WhenDequeueKeepsFailing_JittersCappedDelayResetsAndCancelsAsync()
    {
        using var timeout = CreateTimeout();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var timeProvider = new RecordingTimeProvider();
        using var queue = CreateQueue(timeProvider);
        int dequeueCalls = 0;
        queue.Dequeue = _ => Interlocked.Increment(ref dequeueCalls) == 13
            ? Task.FromResult<IQueueEntry<SimpleWorkItem>?>(null)
            : Task.FromException<IQueueEntry<SimpleWorkItem>?>(new InvalidOperationException("Dequeue failed"));
        await queue.StartWorkingAsync((_, _) => throw new InvalidOperationException("No entry should be processed"), cancellationToken: cancellation.Token);

        var delays = new List<TimeSpan>();
        for (int i = 0; i < 12; i++)
        {
            var delay = await timeProvider.Delays.Reader.ReadAsync(timeout.Token);
            delays.Add(delay);
            Assert.Equal(i + 1, Volatile.Read(ref dequeueCalls));
            timeProvider.Advance(delay);
        }

        var resetDelay = await timeProvider.Delays.Reader.ReadAsync(timeout.Token);
        await cancellation.CancelAsync();
        await queue.Worker.WaitAsync(timeout.Token);

        Assert.Equal(14, dequeueCalls);
        Assert.Equal(13, (await queue.GetQueueStatsAsync()).Errors);
        Assert.InRange(delays[0], TimeSpan.FromMilliseconds(750), TimeSpan.FromMilliseconds(1250));
        Assert.InRange(delays[1], TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(2500));
        Assert.All(delays, delay => Assert.InRange(delay, TimeSpan.FromMilliseconds(750), TimeSpan.FromSeconds(30)));
        Assert.Contains(delays.GetRange(6, 6), delay => delay < TimeSpan.FromSeconds(30));
        Assert.InRange(resetDelay, TimeSpan.FromMilliseconds(750), TimeSpan.FromMilliseconds(1250));
    }

    [Fact]
    public async Task StartWorker_WhenDequeueInvalidatesQueueSetup_RecreatesQueueAndKeepsWorkingAsync()
    {
        using var timeout = CreateTimeout();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var timeProvider = new RecordingTimeProvider();
        using var queue = CreateQueue(timeProvider);
        await queue.EnqueueAsync(new SimpleWorkItem { Data = "Recovered" });

        bool queueCreated = false;
        int setupCalls = 0;
        int dequeueCalls = 0;
        int handlerCalls = 0;
        queue.EnsureCreated = _ =>
        {
            setupCalls++;
            queueCreated = true;
            return Task.CompletedTask;
        };
        queue.BeforeDequeue = () =>
        {
            if (++dequeueCalls == 1)
            {
                queueCreated = false;
                throw new InvalidOperationException("Queue no longer exists");
            }

            if (!queueCreated)
            {
                cancellation.Cancel();
                throw new InvalidOperationException("Queue setup was not restored");
            }
        };

        await queue.StartWorkingAsync((_, _) =>
        {
            handlerCalls++;
            return cancellation.CancelAsync();
        }, autoComplete: true, cancellationToken: cancellation.Token);
        var delay = await timeProvider.Delays.Reader.ReadAsync(timeout.Token);
        timeProvider.Advance(delay);
        await queue.Worker.WaitAsync(timeout.Token);

        var stats = await queue.GetQueueStatsAsync();
        Assert.Equal(3, setupCalls);
        Assert.Equal(2, dequeueCalls);
        Assert.Equal(1, handlerCalls);
        Assert.Equal(1, stats.Errors);
        Assert.Equal(1, stats.Completed);
        Assert.Equal(0, stats.Working);
    }

    private CancellationTokenSource CreateTimeout()
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestCancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        return timeout;
    }

    private WorkerQueue CreateQueue(TimeProvider? timeProvider = null, CircuitBreaker? circuitBreaker = null, int maxAttempts = 3)
    {
        var policy = new ResiliencePolicy { MaxAttempts = maxAttempts, Delay = TimeSpan.Zero, CircuitBreaker = circuitBreaker };
        return new WorkerQueue(new InMemoryQueueOptions<SimpleWorkItem>
        {
            Retries = 0,
            RetryDelay = TimeSpan.Zero,
            WorkItemTimeout = TimeSpan.FromMinutes(5),
            TimeProvider = timeProvider ?? TimeProvider.System,
            LoggerFactory = Log,
            ResiliencePolicyProvider = new ResiliencePolicyProvider().WithPolicy<IQueue<SimpleWorkItem>>(policy)
        });
    }

    private sealed class WorkerQueue(InMemoryQueueOptions<SimpleWorkItem> options) : InMemoryQueue<SimpleWorkItem>(options)
    {
        public Task Worker { get; private set; } = Task.CompletedTask;
        public Func<CancellationToken, Task>? EnsureCreated { get; set; }
        public Action? BeforeDequeue { get; set; }
        public Func<CancellationToken, Task<IQueueEntry<SimpleWorkItem>?>>? Dequeue { get; set; }
        public Func<IQueueEntry<SimpleWorkItem>, bool>? FailComplete { get; set; }
        public Func<IQueueEntry<SimpleWorkItem>, bool>? FailAbandon { get; set; }
        public int CompleteAttempts { get; private set; }
        public int AbandonAttempts { get; private set; }

        protected override void StartWorkingImpl(Func<IQueueEntry<SimpleWorkItem>, CancellationToken, Task> handler, bool autoComplete, CancellationToken cancellationToken)
        {
            Worker = StartWorker(handler, autoComplete, cancellationToken);
        }

        protected override Task EnsureQueueCreatedAsync(CancellationToken cancellationToken = default)
        {
            return EnsureCreated?.Invoke(cancellationToken) ?? base.EnsureQueueCreatedAsync(cancellationToken);
        }

        protected override Task<IQueueEntry<SimpleWorkItem>?> DequeueImplAsync(CancellationToken linkedCancellationToken)
        {
            BeforeDequeue?.Invoke();
            return Dequeue?.Invoke(linkedCancellationToken) ?? base.DequeueImplAsync(linkedCancellationToken);
        }

        public override Task CompleteAsync(IQueueEntry<SimpleWorkItem> queueEntry)
        {
            CompleteAttempts++;
            if (FailComplete?.Invoke(queueEntry) == true)
                throw new InvalidOperationException("Completion failed before settlement");

            return base.CompleteAsync(queueEntry);
        }

        public override Task AbandonAsync(IQueueEntry<SimpleWorkItem> queueEntry)
        {
            AbandonAttempts++;
            if (FailAbandon?.Invoke(queueEntry) == true)
                throw new InvalidOperationException("Abandonment failed before settlement");

            return base.AbandonAsync(queueEntry);
        }
    }

    private sealed class RecordingTimeProvider : FakeTimeProvider
    {
        public Channel<TimeSpan> Delays { get; } = Channel.CreateUnbounded<TimeSpan>();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Delays.Writer.TryWrite(dueTime);
            return timer;
        }
    }
}

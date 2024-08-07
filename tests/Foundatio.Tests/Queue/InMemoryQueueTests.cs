using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Queues;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Queue;

public class InMemoryQueueTests : QueueTestBase
{
    private IQueue<SimpleWorkItem> _queue;

    public InMemoryQueueTests(ITestOutputHelper output) : base(output) { }

    protected override IQueue<SimpleWorkItem> GetQueue(int retries = 1, TimeSpan? workItemTimeout = null, TimeSpan? retryDelay = null, int[] retryMultipliers = null, int deadLetterMaxItems = 100, bool runQueueMaintenance = true, TimeProvider timeProvider = null)
    {
        if (_queue == null)
            _queue = new InMemoryQueue<SimpleWorkItem>(o => o
                .RetryDelay(retryDelay.GetValueOrDefault(TimeSpan.FromMinutes(1)))
                .Retries(retries)
                .RetryMultipliers(retryMultipliers ?? new[] { 1, 3, 5, 10 })
                .WorkItemTimeout(workItemTimeout.GetValueOrDefault(TimeSpan.FromMinutes(5)))
                .TimeProvider(timeProvider)
                .LoggerFactory(Log));
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Queue Id: {QueueId}", _queue.QueueId);
        return _queue;
    }

    protected override async Task CleanupQueueAsync(IQueue<SimpleWorkItem> queue)
    {
        if (queue == null)
            return;

        try
        {
            await queue.DeleteQueueAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up queue");
        }
    }

    [Fact]
    public async Task TestAsyncEvents()
    {
        using var q = new InMemoryQueue<SimpleWorkItem>(o => o.LoggerFactory(Log));
        var disposables = new List<IDisposable>(5);
        try
        {
            disposables.Add(q.Enqueuing.AddHandler(async (sender, args) =>
            {
                await Task.Delay(250);
                _logger.LogInformation("First Enqueuing");
            }));
            disposables.Add(q.Enqueuing.AddHandler(async (sender, args) =>
            {
                await Task.Delay(250);
                _logger.LogInformation("Second Enqueuing");
            }));
            disposables.Add(q.Enqueued.AddHandler(async (sender, args) =>
            {
                await Task.Delay(250);
                _logger.LogInformation("First");
            }));
            disposables.Add(q.Enqueued.AddHandler(async (sender, args) =>
            {
                await Task.Delay(250);
                _logger.LogInformation("Second");
            }));

            var sw = Stopwatch.StartNew();
            await q.EnqueueAsync(new SimpleWorkItem());
            sw.Stop();
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Time {Elapsed:g}", sw.Elapsed);

            sw.Restart();
            await q.EnqueueAsync(new SimpleWorkItem());
            sw.Stop();
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Time {Elapsed:g}", sw.Elapsed);
        }
        finally
        {
            foreach (var disposable in disposables)
                disposable.Dispose();
        }
    }

    [Fact]
    public async Task CanGetCompletedEntries()
    {
        using var q = new InMemoryQueue<SimpleWorkItem>(o => o.LoggerFactory(Log).CompletedEntryRetentionLimit(10));

        await q.EnqueueAsync(new SimpleWorkItem());
        Assert.Single(q.GetEntries());
        Assert.Empty(q.GetDequeuedEntries());
        Assert.Empty(q.GetCompletedEntries());

        var item = await q.DequeueAsync();
        Assert.Empty(q.GetEntries());
        Assert.Single(q.GetDequeuedEntries());
        Assert.Empty(q.GetCompletedEntries());

        await item.CompleteAsync();
        Assert.Empty(q.GetEntries());
        Assert.Empty(q.GetDequeuedEntries());
        Assert.Single(q.GetCompletedEntries());

        for (int i = 0; i < 100; i++)
        {
            await q.EnqueueAsync(new SimpleWorkItem());
            item = await q.DequeueAsync();
            await item.CompleteAsync();
        }

        Assert.Empty(q.GetEntries());
        Assert.Empty(q.GetDequeuedEntries());
        Assert.Equal(10, q.GetCompletedEntries().Count);
    }

    [Fact]
    public override Task CanQueueAndDequeueWorkItemAsync()
    {
        return base.CanQueueAndDequeueWorkItemAsync();
    }

    [Fact]
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

    [Fact]
    public override Task CanDequeueWithLockingAsync()
    {
        return base.CanDequeueWithLockingAsync();
    }

    [Fact]
    public override Task CanHaveMultipleQueueInstancesWithLockingAsync()
    {
        return base.CanHaveMultipleQueueInstancesWithLockingAsync();
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

    #region Issue239

    class QueueEntry_Issue239<T> : IQueueEntry<T> where T : class
    {
        IQueueEntry<T> _queueEntry;

        public QueueEntry_Issue239(IQueueEntry<T> queueEntry)
        {
            _queueEntry = queueEntry;
        }

        public T Value => _queueEntry.Value;

        public string Id => _queueEntry.Id;

        public string CorrelationId => _queueEntry.CorrelationId;

        public IDictionary<string, string> Properties => _queueEntry.Properties;

        public Type EntryType => _queueEntry.EntryType;

        public bool IsCompleted => _queueEntry.IsCompleted;

        public bool IsAbandoned => _queueEntry.IsAbandoned;

        public int Attempts => _queueEntry.Attempts;

        public Task AbandonAsync()
        {
            return _queueEntry.AbandonAsync();
        }

        public Task CompleteAsync()
        {
            return _queueEntry.CompleteAsync();
        }

        public ValueTask DisposeAsync()
        {
            return _queueEntry.DisposeAsync();
        }

        public object GetValue()
        {
            return _queueEntry.GetValue();
        }

        public void MarkAbandoned()
        {
            // we want to simulate timing of user complete call between the maintenance abandon call to _dequeued.TryRemove and entry.MarkAbandoned();
            Task.Delay(1500).Wait();

            _queueEntry.MarkAbandoned();
        }

        public void MarkCompleted()
        {
            _queueEntry.MarkCompleted();
        }

        public Task RenewLockAsync()
        {
            return _queueEntry.RenewLockAsync();
        }
    }

    class InMemoryQueue_Issue239<T> : InMemoryQueue<T> where T : class
    {
        public override Task AbandonAsync(IQueueEntry<T> entry)
        {
            // delay first abandon from maintenance (simulate timing issues which may occur to demonstrate the problem)
            return base.AbandonAsync(new QueueEntry_Issue239<T>(entry));
        }

        public InMemoryQueue_Issue239(ILoggerFactory loggerFactory)
            : base(o => o
                .RetryDelay(TimeSpan.FromMinutes(1))
                .Retries(1)
                .RetryMultipliers(new[] { 1, 3, 5, 10 })
                .LoggerFactory(loggerFactory)
                .WorkItemTimeout(TimeSpan.FromMilliseconds(100)))
        {
        }
    }

    [Fact]
    // this test reproduce an issue which cause worker task loop to crash and stop processing items when auto abandoned item is ultimately processed and user call complete on
    // https://github.com/FoundatioFx/Foundatio/issues/239
    public virtual async Task CompleteOnAutoAbandonedHandledProperly_Issue239()
    {
        // create queue with short work item timeout, so it will be auto abandoned
        var queue = new InMemoryQueue_Issue239<SimpleWorkItem>(Log);
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // completion source to wait for CompleteAsync call before to assert
        var taskCompletionSource = new TaskCompletionSource<bool>();

        // start handling items
        await queue.StartWorkingAsync(async (item) =>
        {
            // we want to wait for maintenance to be performed and auto abandon our item, we don't have any way for waiting in IQueue so we'll settle for a delay
            if (item.Value.Data == "Delay")
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
            }

            try
            {
                // call complete on the auto abandoned item
                await item.CompleteAsync();
            }
            finally
            {
                // completeAsync will currently throw an exception becuase item can not be removed from dequeued list because it was already removed due to auto abandon
                // infrastructure handles user exception incorrectly
                taskCompletionSource.SetResult(true);
            }
        }, cancellationToken: cancellationTokenSource.Token);

        // enqueue item which will be processed after it's auto abandoned
        await queue.EnqueueAsync(new SimpleWorkItem() { Data = "Delay" });

        // wait for taskCompletionSource.SetResult to be called or timeout after 1 second
        bool timedout = (await Task.WhenAny(taskCompletionSource.Task, Task.Delay(TimeSpan.FromSeconds(2)))) != taskCompletionSource.Task;
        Assert.False(timedout);

        // enqueue another item and make sure it was handled (worker loop didn't crash)
        taskCompletionSource = new TaskCompletionSource<bool>();
        await queue.EnqueueAsync(new SimpleWorkItem() { Data = "No Delay" });

        // one option to fix this issue is surrounding the AbandonAsync call in StartWorkingImpl exception handler in inner try/catch block
        timedout = (await Task.WhenAny(taskCompletionSource.Task, Task.Delay(TimeSpan.FromSeconds(2)))) != taskCompletionSource.Task;
        Assert.False(timedout);
    }

    #endregion
}

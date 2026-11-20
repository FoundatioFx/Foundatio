using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Lock;
using Foundatio.Messaging;
using Foundatio.Queues;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Jobs;

internal sealed class WorkItemExecution(IMessagePublisher publisher, IWorkItemHandler handler, IQueueEntry<WorkItemData> entry, Type dataType, ILock workItemLock, CancellationToken cancellationToken) : IDisposable
{
    private RenewalState? _renewals = workItemLock.GetType() == typeof(EmptyLock) ? null : new RenewalState(CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
    private int _stopped;

    public CancellationToken GetHandlerToken(CancellationToken hostToken) => _renewals?.Cancellation?.Token ?? hostToken;
    public bool AbandonAttempted { get; private set; }
    public JobResult? SettledResult { get; private set; }

    public async Task ReportProgressAsync(int progress, string? message)
    {
        bool renewOnProgress = handler.AutoRenewLockOnProgress;
        if (!IsProcessing(throwIfAbandoned: renewOnProgress))
            return;

        if (renewOnProgress)
        {
            if (!await RenewAsync(includeQueueEntry: true).AnyContext() || !IsProcessing(throwIfAbandoned: true))
                return;
        }
        else
        {
            ThrowIfRenewalFailed();
        }

        await PublishProgressAsync(publisher, handler, entry, progress, message).AnyContext();
        if (handler.Log.IsEnabled(LogLevel.Information))
            handler.Log.LogInformation("{TypeName} Progress {Progress}%: {Message}", dataType.Name, progress, message);

        if (renewOnProgress && Volatile.Read(ref _stopped) == 0)
        {
            ThrowIfRenewalFailed();
            ThrowIfEntryAbandoned();
        }
    }

    public async Task RenewLockAsync()
    {
        if (!await RenewAsync(includeQueueEntry: false).AnyContext())
            throw new InvalidOperationException("The work item is no longer being processed");
    }

    internal static async Task PublishProgressAsync(IMessagePublisher publisher, IWorkItemHandler handler, IQueueEntry<WorkItemData> entry, int progress, string? message)
    {
        try
        {
            await publisher.PublishAsync(new WorkItemStatus
            {
                WorkItemId = entry.Value?.WorkItemId,
                Type = entry.Value?.Type,
                Progress = progress,
                Message = message
            }).AnyContext();
        }
        catch (Exception ex)
        {
            handler.Log.LogError(ex, "Error sending progress report: {Message}", ex.Message);
        }
    }

    private bool IsProcessing(bool throwIfAbandoned)
    {
        if (Volatile.Read(ref _stopped) != 0)
            return false;

        if (throwIfAbandoned)
            ThrowIfEntryAbandoned();
        else if (entry.IsAbandoned)
            return false;

        return !entry.IsCompleted;
    }

    private void ThrowIfEntryAbandoned()
    {
        if (entry.IsAbandoned)
            throw new InvalidOperationException("The queue entry was abandoned before processing finished");
    }

    private async Task<bool> RenewAsync(bool includeQueueEntry)
    {
        var renewals = LazyInitializer.EnsureInitialized(ref _renewals, static () => new RenewalState(null));
        lock (renewals)
        {
            if (!IsProcessing(throwIfAbandoned: includeQueueEntry))
                return false;

            renewals.Failure?.Throw();
            renewals.ActiveCount++;
        }

        try
        {
            if (includeQueueEntry)
                await Task.WhenAll(RenewQueueEntryAsync(), RenewWorkItemLockAsync(renewals)).AnyContext();
            else
                await RenewWorkItemLockAsync(renewals).AnyContext();
        }
        catch (Exception ex)
        {
            if (renewals.Failure is { } failure)
            {
                if (!ReferenceEquals(failure.SourceException, ex))
                    Activity.Current?.AddException(ex);

                failure.Throw();
            }

            throw;
        }
        finally
        {
            lock (renewals)
            {
                if (--renewals.ActiveCount == 0)
                    renewals.Drained?.TrySetResult();
            }
        }

        renewals.Failure?.Throw();
        return true;
    }

    private async Task RenewQueueEntryAsync()
    {
        try
        {
            await entry.RenewLockAsync().AnyContext();
        }
        catch (Exception ex)
        {
            handler.Log.LogError(ex, "Unable to renew queue entry {QueueEntryId}", entry.Id);
        }
    }

    private async Task RenewWorkItemLockAsync(RenewalState renewals)
    {
        try
        {
            await workItemLock.RenewAsync().AnyContext();
        }
        catch (Exception ex)
        {
            await renewals.RecordFailureAsync(ex).AnyContext();
        }
    }

    public void ThrowIfRenewalFailed()
    {
        Volatile.Read(ref _renewals)?.Failure?.Throw();
    }

    public Exception GetProcessingError(Exception error, Activity? activity)
    {
        if (error is OperationCanceledException && Volatile.Read(ref _renewals)?.Failure is { } failure && !ReferenceEquals(error, failure.SourceException))
        {
            activity?.AddException(error);
            return failure.SourceException;
        }

        return error;
    }

    public Task SettleAsync(JobResult result)
    {
        bool completing = result.IsSuccess;
        AbandonAttempted = !completing;
        if (!entry.Value.SendProgressReports)
            return completing ? entry.CompleteAsync() : entry.AbandonAsync();

        return SettleAndRecordAsync(result, completing);
    }

    private async Task SettleAndRecordAsync(JobResult result, bool completing)
    {
        using var attempt = new QueueEntrySettlement<WorkItemData>(entry, completing);
        Task? operation = null;
        try
        {
            operation = completing ? entry.CompleteAsync() : entry.AbandonAsync();
            await operation.AnyContext();
        }
        catch when (attempt.IsConfirmedFor(operation))
        {
            SettledResult = result;
            throw;
        }

        SettledResult = result;
    }

    public Task StopAndDrainRenewalsAsync()
    {
        // A concurrent first renewal either sees the stop or has already registered its state.
        Interlocked.Exchange(ref _stopped, 1);
        var renewals = Volatile.Read(ref _renewals);
        if (renewals is null)
            return Task.CompletedTask;

        lock (renewals)
        {
            if (renewals.ActiveCount == 0)
                return Task.CompletedTask;

            return (renewals.Drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
    }

    public void Dispose() => _renewals?.Cancellation?.Dispose();

    // Concurrent renewals share the first ownership error. The monitor guards the active count and drain signal.
    private sealed class RenewalState(CancellationTokenSource? cancellation)
    {
        private ExceptionDispatchInfo? _failure;
        public CancellationTokenSource? Cancellation { get; } = cancellation;
        public ExceptionDispatchInfo? Failure => Volatile.Read(ref _failure);
        public int ActiveCount { get; set; }
        public TaskCompletionSource? Drained { get; set; }

        public async Task RecordFailureAsync(Exception error)
        {
            if (Interlocked.CompareExchange(ref _failure, ExceptionDispatchInfo.Capture(error), null) is not null || Cancellation is null)
                return;

            try
            {
                await Cancellation.CancelAsync().AnyContext();
            }
            catch (Exception callbackError)
            {
                Activity.Current?.AddException(callbackError);
            }
        }
    }
}

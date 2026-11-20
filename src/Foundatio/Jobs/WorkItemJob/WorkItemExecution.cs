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

/// <summary>
/// Coordinates lock renewals for one delivery handled by <see cref="WorkItemJob"/>. A handler can start
/// overlapping renewals or catch a renewal error. The job waits for those renewals and checks ownership
/// before it settles the entry and releases the work-item lock.
/// </summary>
internal sealed class WorkItemExecution(IMessagePublisher publisher, IWorkItemHandler handler, IQueueEntry<WorkItemData> entry, Type dataType, ILock workItemLock, CancellationToken cancellationToken) : IDisposable
{
    private RenewalState? _renewals = workItemLock.GetType() == typeof(EmptyLock) ? null : new RenewalState(CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
    private int _stopped;

    public CancellationToken GetHandlerToken(CancellationToken hostToken) => _renewals?.Cancellation?.Token ?? hostToken;

    public async Task RunHandlerAsync(WorkItemContext context)
    {
        try
        {
            await handler.HandleItemAsync(context).AnyContext();
        }
        finally
        {
            await StopAndDrainRenewalsAsync().AnyContext();
        }

        // The handler may have caught the renewal exception and returned its default success result.
        ThrowIfRenewalFailed();
    }

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

    public Exception GetProcessingError(Exception error, Activity? activity)
    {
        // A handler observing cancellation can obscure the provider exception that triggered it.
        if (error is OperationCanceledException && Volatile.Read(ref _renewals)?.Failure is { } failure && !ReferenceEquals(error, failure.SourceException))
        {
            activity?.AddException(error);
            return failure.SourceException;
        }

        return error;
    }

    public void Dispose() => _renewals?.Cancellation?.Dispose();

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
        catch (Exception ex) when (renewals.Failure is { } failure)
        {
            // Even a failing diagnostic logger must leave the original loss of ownership visible to the handler.
            if (!ReferenceEquals(failure.SourceException, ex))
                Activity.Current?.AddException(ex);

            failure.Throw();
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
            if (!renewals.TryRecordFailure(ex))
                return;

            if (renewals.Cancellation is { } cancellation)
                await CancelHandlerAsync(cancellation).AnyContext();

            handler.Log.LogWarning(ex, "Unable to renew work-item lock for queue entry {QueueEntryId}", entry.Id);
        }
    }

    private async Task CancelHandlerAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await cancellation.CancelAsync().AnyContext();
        }
        catch (AggregateException ex)
        {
            Activity.Current?.AddException(ex);
            handler.Log.LogWarning(ex, "Cancellation callback failed after losing the work-item lock for queue entry {QueueEntryId}", entry.Id);
        }
    }

    private void ThrowIfRenewalFailed()
    {
        Volatile.Read(ref _renewals)?.Failure?.Throw();
    }

    private Task StopAndDrainRenewalsAsync()
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

            // The last active renewal signals this task. Resume settlement outside the monitor.
            return (renewals.Drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
    }

    // The monitor protects registration and the drain signal. All provider I/O runs outside it.
    private sealed class RenewalState(CancellationTokenSource? cancellation)
    {
        private ExceptionDispatchInfo? _failure;
        public CancellationTokenSource? Cancellation { get; } = cancellation;
        public ExceptionDispatchInfo? Failure => Volatile.Read(ref _failure);
        public int ActiveCount { get; set; }
        public TaskCompletionSource? Drained { get; set; }

        public bool TryRecordFailure(Exception error)
        {
            // Later callers must see the first exception with the stack from the provider that threw it.
            return Interlocked.CompareExchange(ref _failure, ExceptionDispatchInfo.Capture(error), null) is null;
        }
    }
}

using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Lock;
using Foundatio.Queues;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Jobs;

internal sealed class WorkItemExecution(WorkItemJob job, IWorkItemHandler handler, IQueueEntry<WorkItemData> entry, Type dataType, ILock workItemLock, CancellationToken cancellationToken) : IDisposable
{
    private Renewals? _renewals = workItemLock.GetType() == typeof(EmptyLock) ? null : new Renewals(CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
    private int _stopped;

    public CancellationToken GetHandlerToken(CancellationToken hostToken) => _renewals?.Cancellation?.Token ?? hostToken;
    public Exception? RenewalError => Volatile.Read(ref _renewals)?.Failure?.SourceException;
    private bool HasStopped => Volatile.Read(ref _stopped) != 0 || entry.IsAbandoned || entry.IsCompleted;

    public async Task ReportProgressAsync(int progress, string? message)
    {
        if (HasStopped)
            return;

        if (handler.AutoRenewLockOnProgress)
        {
            if (!await RenewAsync(includeQueueEntry: true).AnyContext() || HasStopped)
                return;
        }
        else
        {
            ThrowIfRenewalFailed();
        }

        await job.ReportHandlerProgressAsync(handler, entry, dataType, progress, message).AnyContext();
    }

    public async Task RenewLockAsync()
    {
        if (!await RenewAsync(includeQueueEntry: false).AnyContext())
            throw new InvalidOperationException("The work item is no longer being processed");
    }

    private async Task<bool> RenewAsync(bool includeQueueEntry)
    {
        var renewals = LazyInitializer.EnsureInitialized(ref _renewals, static () => new Renewals(null));
        lock (renewals)
        {
            if (HasStopped)
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
        catch
        {
            // A cancellation callback or queue logger can also throw. Preserve the ownership failure.
            renewals.Failure?.Throw();
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

    private async Task RenewWorkItemLockAsync(Renewals renewals)
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

    public Task StopAsync()
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

    private sealed class Renewals(CancellationTokenSource? cancellation)
    {
        private ExceptionDispatchInfo? _failure;
        public CancellationTokenSource? Cancellation { get; } = cancellation;
        public ExceptionDispatchInfo? Failure => Volatile.Read(ref _failure);
        public int ActiveCount { get; set; }
        public TaskCompletionSource? Drained { get; set; }

        public Task RecordFailureAsync(Exception error)
        {
            return Interlocked.CompareExchange(ref _failure, ExceptionDispatchInfo.Capture(error), null) is null && Cancellation is not null
                ? Cancellation.CancelAsync()
                : Task.CompletedTask;
        }
    }
}

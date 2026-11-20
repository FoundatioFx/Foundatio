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
/// before it settles the entry and releases the work-item lock. Provider calls run outside the lock so renewals can overlap.
/// </summary>
internal sealed class WorkItemExecution(IMessagePublisher publisher, IWorkItemHandler handler, IQueueEntry<WorkItemData> entry, Type dataType, ILock workItemLock, ILogger logger, CancellationToken cancellationToken) : IDisposable
{
    private readonly ILogger _logger = logger;
    private readonly System.Threading.Lock _renewalLock = new();
    private readonly CancellationTokenSource? _cancellation = workItemLock.GetType() == typeof(EmptyLock) ? null : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    private volatile ExceptionDispatchInfo? _renewalFailure;
    private volatile bool _stopped;
    private int _activeRenewals;
    private TaskCompletionSource? _renewalsDrained;

    public CancellationToken GetHandlerToken(CancellationToken hostToken) => _cancellation?.Token ?? hostToken;

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
        _renewalFailure?.Throw();
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
            _renewalFailure?.Throw();
        }

        await PublishProgressAsync(publisher, _logger, entry, progress, message).AnyContext();
        if (handler.Log.IsEnabled(LogLevel.Information))
            handler.Log.LogInformation("{TypeName} Progress {Progress}%: {Message}", dataType.Name, progress, message);

        if (renewOnProgress && !_stopped)
        {
            _renewalFailure?.Throw();
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
        if (error is OperationCanceledException && _renewalFailure is { } failure && !ReferenceEquals(error, failure.SourceException))
        {
            activity?.AddException(error);
            return failure.SourceException;
        }

        return error;
    }

    public void Dispose() => _cancellation?.Dispose();

    internal static async Task PublishProgressAsync(IMessagePublisher publisher, ILogger logger, IQueueEntry<WorkItemData> entry, int progress = 0, string? message = null)
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
            logger.LogError(ex, "Error sending progress report: {Message}", ex.Message);
        }
    }

    private bool IsProcessing(bool throwIfAbandoned)
    {
        if (_stopped)
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
        if (!IsProcessing(throwIfAbandoned: includeQueueEntry))
            return false;

        lock (_renewalLock)
        {
            if (_stopped)
                return false;

            _renewalFailure?.Throw();
            _activeRenewals++;
        }

        try
        {
            if (includeQueueEntry)
                await Task.WhenAll(RenewQueueEntryAsync(), RenewWorkItemLockAsync()).AnyContext();
            else
                await RenewWorkItemLockAsync().AnyContext();
        }
        catch (Exception ex) when (_renewalFailure is not null)
        {
            // Preserve the provider failure if its diagnostics also throw.
            Activity.Current?.AddException(ex);
        }
        finally
        {
            lock (_renewalLock)
            {
                if (--_activeRenewals == 0)
                    _renewalsDrained?.SetResult();
            }
        }

        _renewalFailure?.Throw();
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
            LogRenewalFailure(ex);
        }
    }

    private async Task RenewWorkItemLockAsync()
    {
        try
        {
            await workItemLock.RenewAsync().AnyContext();
        }
        catch (Exception ex)
        {
            lock (_renewalLock)
            {
                if (_renewalFailure is not null)
                    return;

                _renewalFailure = ExceptionDispatchInfo.Capture(ex);
            }

            // Signal loss immediately so a slow cancellation callback cannot delay the renewal error log.
            var callbacks = _cancellation?.CancelAsync() ?? Task.CompletedTask;
            try
            {
                LogRenewalFailure(ex, workItemLock);
            }
            finally
            {
                await ObserveCancellationCallbacksAsync(callbacks).AnyContext();
            }
        }
    }

    private void LogRenewalFailure(Exception ex, ILock? failedWorkItemLock = null)
    {
        var scope = new LogState().Property("QueueEntryId", entry.Id).Property("RenewalTarget", failedWorkItemLock is null ? "QueueEntry" : "WorkItemLock");
        if (failedWorkItemLock is not null)
            scope.Property("Resource", failedWorkItemLock.Resource);

        using var _ = _logger.BeginScope(scope);
        _logger.LogError(ex, "Error renewing work item locks: {Message}", ex.Message);
    }

    private async Task ObserveCancellationCallbacksAsync(Task callbacks)
    {
        try
        {
            await callbacks.AnyContext();
        }
        catch (AggregateException ex)
        {
            Activity.Current?.AddException(ex);
            _logger.LogWarning(ex, "Cancellation callback failed after losing the work-item lock for queue entry {QueueEntryId}", entry.Id);
        }
    }

    private Task StopAndDrainRenewalsAsync()
    {
        lock (_renewalLock)
        {
            _stopped = true;
            if (_activeRenewals == 0)
                return Task.CompletedTask;

            // The last renewal resumes settlement after its provider call finishes.
            _renewalsDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _renewalsDrained.Task;
        }
    }
}

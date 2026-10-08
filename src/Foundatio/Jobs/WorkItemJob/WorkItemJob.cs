using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Lock;
using Foundatio.Messaging;
using Foundatio.Queues;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Jobs;

[Job(Description = "Processes adhoc work item queues entries")]
public class WorkItemJob : IQueueJob<WorkItemData>, IHaveLogger, IHaveLoggerFactory
{
    protected readonly IMessagePublisher _publisher;
    protected readonly WorkItemHandlers _handlers;
    protected readonly IQueue<WorkItemData> _queue;
    protected readonly ILogger _logger;
    protected readonly ILoggerFactory _loggerFactory;

    public WorkItemJob(IQueue<WorkItemData> queue, IMessagePublisher publisher, WorkItemHandlers handlers, ILoggerFactory? loggerFactory = null)
    {
        _publisher = publisher;
        _handlers = handlers;
        _queue = queue;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger(GetType());
    }

    public string JobId { get; } = Guid.NewGuid().ToString("N").Substring(0, 10);
    IQueue<WorkItemData> IQueueJob<WorkItemData>.Queue => _queue;
    ILogger IHaveLogger.Logger => _logger;
    ILoggerFactory IHaveLoggerFactory.LoggerFactory => _loggerFactory;

    public virtual async Task<JobResult> RunAsync(CancellationToken cancellationToken = default)
    {
        IQueueEntry<WorkItemData>? queueEntry;

        using var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            queueEntry = await _queue.DequeueAsync(linkedCancellationTokenSource.Token).AnyContext();
        }
        catch (OperationCanceledException)
        {
            return JobResult.Cancelled;
        }
        catch (Exception ex)
        {
            return JobResult.FromException(ex, $"Error trying to dequeue work item: {ex.Message}");
        }

        if (cancellationToken.IsCancellationRequested && queueEntry is null)
            return JobResult.Cancelled;

        if (queueEntry is null)
            return JobResult.SuccessWithMessage("No queue entry to process.");

        return await ProcessAsync(queueEntry, cancellationToken).AnyContext();
    }

    public async Task<JobResult> ProcessAsync(IQueueEntry<WorkItemData> queueEntry, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            await queueEntry.AbandonAsync().AnyContext();
            return JobResult.CancelledWithMessage($"Abandoning {queueEntry.Value?.Type} work item: {queueEntry.Id}");
        }

        var workItemDataType = GetWorkItemType(queueEntry.Value?.Type);
        if (workItemDataType is null)
        {
            await queueEntry.AbandonAsync().AnyContext();
            return JobResult.FailedWithMessage($"Abandoning {queueEntry.Value?.Type} work item: {queueEntry.Id}: Could not resolve work item data type");
        }

        using var activity = StartProcessWorkItemActivity(queueEntry, workItemDataType);
        using var _ = _logger.BeginScope(s => s
            .Property("JobId", JobId)
            .Property("QueueEntryId", queueEntry.Id)
            .PropertyIf("CorrelationId", queueEntry.CorrelationId, !String.IsNullOrEmpty(queueEntry.CorrelationId))
            .Property("QueueEntryName", workItemDataType.Name));

        object? workItemData;
        try
        {
            workItemData = _queue.Serializer.Deserialize(queueEntry.Value!.Data, workItemDataType);
        }
        catch (Exception ex)
        {
            activity?.SetErrorStatus(ex, $"Abandoning {queueEntry.Value!.Type} work item: {queueEntry.Id}: Failed to parse {workItemDataType.Name} work item data");
            await queueEntry.AbandonAsync().AnyContext();
            return JobResult.FromException(ex, $"Abandoning {queueEntry.Value!.Type} work item: {queueEntry.Id}: Failed to parse {workItemDataType.Name} work item data");
        }

        if (workItemData is null)
        {
            _logger.LogWarning("Abandoning {TypeName} work item: {Id}: Deserialization returned null for {WorkItemDataType}", queueEntry.Value.Type, queueEntry.Id, workItemDataType.Name);
            await queueEntry.AbandonAsync().AnyContext();
            return JobResult.FailedWithMessage($"Abandoning {queueEntry.Value.Type} work item: {queueEntry.Id}: Deserialization returned null for {workItemDataType.Name}");
        }

        var handler = _handlers.GetHandler(workItemDataType);
        if (handler is null)
        {
            await queueEntry.CompleteAsync().AnyContext();
            var result = JobResult.FailedWithMessage($"Completing {queueEntry.Value.Type} work item: {queueEntry.Id}: Handler for type {workItemDataType.Name} not registered");
            activity?.SetErrorStatus(message: result.Message);
            return result;
        }

        if (queueEntry.Value.SendProgressReports)
            await ReportProgressAsync(handler, queueEntry).AnyContext();

        var lockValue = await handler.GetWorkItemLockAsync(workItemData, cancellationToken).AnyContext();
        if (lockValue is null)
        {
            handler.Log.LogInformation("Abandoning {TypeName} work item: {Id}: Unable to acquire work item lock", queueEntry.Value.Type, queueEntry.Id);

            await queueEntry.AbandonAsync().AnyContext();
            return JobResult.CancelledWithMessage($"Unable to acquire work item lock. Abandoning {queueEntry.Value.Type} queue entry: {queueEntry.Id}");
        }

        using var workItemCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var leaseLoss = new WorkItemLeaseLoss(workItemCancellation);
        var progressCallback = new Func<int, string?, Task>(async (progress, message) =>
        {
            if (handler.AutoRenewLockOnProgress)
                await RenewWorkItemLocksAsync(handler, queueEntry, lockValue, leaseLoss).AnyContext();

            await ReportProgressAsync(handler, queueEntry, progress, message).AnyContext();
            handler.Log.LogInformation("{TypeName} Progress {Progress}%: {Message}", workItemDataType.Name, progress, message);
        });

        try
        {
            handler.LogProcessingQueueEntry(queueEntry, workItemDataType, workItemData);
            var workItemContext = new WorkItemContext(workItemData, JobId, lockValue, workItemCancellation.Token, progressCallback);
            await handler.HandleItemAsync(workItemContext).AnyContext();

            // A handler that stops cooperatively after a lost lease (returning early or swallowing the cancellation)
            // hasn't finished the work, and another worker may now own it: never acknowledge it, even if the job
            // was also cancelled meanwhile
            if (leaseLoss.IsLost)
            {
                if (!queueEntry.IsAbandoned && !queueEntry.IsCompleted)
                    await queueEntry.AbandonAsync().AnyContext();

                var lostResult = JobResult.FailedWithMessage($"Abandoning {queueEntry.Value.Type} work item: {queueEntry.Id}: Lost work item lease in handler {workItemDataType.Name}");
                activity?.SetErrorStatus(message: lostResult.Message);
                return lostResult;
            }

            if (!workItemContext.Result.IsSuccess)
            {
                if (!queueEntry.IsAbandoned && !queueEntry.IsCompleted)
                {
                    await queueEntry.AbandonAsync().AnyContext();
                    return workItemContext.Result;
                }
            }

            if (!queueEntry.IsAbandoned && !queueEntry.IsCompleted)
            {
                await queueEntry.CompleteAsync().AnyContext();
                handler.LogAutoCompletedQueueEntry(queueEntry, workItemDataType, workItemData);
            }

            if (queueEntry.Value.SendProgressReports)
                await ReportProgressAsync(handler, queueEntry, 100).AnyContext();

            return JobResult.Success;
        }
        catch (Exception ex)
        {
            activity?.SetErrorStatus(ex);

            if (queueEntry.Value.SendProgressReports)
                await ReportProgressAsync(handler, queueEntry, -1, $"Failed: {ex.Message}").AnyContext();

            if (!queueEntry.IsAbandoned && !queueEntry.IsCompleted)
            {
                await queueEntry.AbandonAsync().AnyContext();
                return JobResult.FromException(ex, $"Abandoning {queueEntry.Value.Type} work item: {queueEntry.Id}: Error in handler {workItemDataType.Name}");
            }

            return JobResult.FromException(ex, $"Error processing {queueEntry.Value.Type} work item: {queueEntry.Id} in handler: {workItemDataType.Name}");
        }
        finally
        {
            await lockValue.ReleaseAsync().AnyContext();
        }
    }

    /// <summary>
    /// Renews the queue entry and work item locks. Renewal errors are logged so progress reporting continues,
    /// but a lost work item lock cancels <see cref="WorkItemContext.CancellationToken"/> because another
    /// process may now own the work, and the job then abandons the entry instead of completing it.
    /// </summary>
    /// <remarks>
    /// A failed queue entry renewal doesn't cancel the work item: queues report only provider errors, not a definite
    /// loss, and while the work item lock is held a redelivered entry can't be processed concurrently because its
    /// worker fails to acquire the same lock and abandons it.
    /// </remarks>
    private static async Task RenewWorkItemLocksAsync(IWorkItemHandler handler, IQueueEntry<WorkItemData> queueEntry, ILock workItemLock, WorkItemLeaseLoss leaseLoss)
    {
        var queueEntryRenewal = RenewQueueEntryLockAsync();
        var workItemLockRenewal = RenewWorkItemLockAsync();

        try
        {
            await Task.WhenAll(queueEntryRenewal, workItemLockRenewal).AnyContext();
        }
        catch (Exception ex) when (workItemLockRenewal.Exception?.InnerException is not LockException)
        {
            handler.Log.LogError(ex, "Error renewing work item locks: {Message}", ex.Message);
        }
        catch
        {
            if (queueEntryRenewal.Exception?.InnerException is { } queueError)
                handler.Log.LogError(queueError, "Error renewing queue entry lock: {Message}", queueError.Message);
        }

        if (workItemLockRenewal.Exception?.InnerException is not LockException)
            return;

        handler.Log.LogWarning("Lost work item lock {Resource} for queue entry {Id}, cancelling work item", workItemLock.Resource, queueEntry.Id);
        await leaseLoss.SignalAsync().AnyContext();

        async Task RenewQueueEntryLockAsync() => await queueEntry.RenewLockAsync().AnyContext();
        async Task RenewWorkItemLockAsync() => await workItemLock.RenewAsync().AnyContext();
    }

    /// <summary>
    /// Records that the work item lease was lost and cancels the work item. The flag, not the token, decides whether
    /// the entry may be completed, because the job's own token can be cancelled at any time afterward.
    /// </summary>
    private sealed class WorkItemLeaseLoss(CancellationTokenSource workItemCancellation)
    {
        private volatile bool _isLost;

        public bool IsLost => _isLost;

        public async Task SignalAsync()
        {
            _isLost = true;
            try
            {
                await workItemCancellation.CancelAsync().AnyContext();
            }
            catch (ObjectDisposedException)
            {
                // A progress report that wasn't awaited can finish after the work item; nothing is left to cancel
            }
        }
    }

    protected virtual Activity? StartProcessWorkItemActivity(IQueueEntry<WorkItemData> entry, Type workItemDataType)
    {
        var activity = FoundatioDiagnostics.ActivitySource.StartActivity("ProcessQueueEntry", ActivityKind.Internal, entry.CorrelationId);
        if (activity is null)
            return null;

        if (entry.Properties is not null && entry.Properties.TryGetValue("TraceState", out string? traceState))
            activity.TraceStateString = traceState;

        activity.DisplayName = $"Work Item: {entry.Value?.SubMetricName ?? workItemDataType.Name}";

        EnrichProcessWorkItemActivity(activity, entry, workItemDataType);

        return activity;
    }

    protected virtual void EnrichProcessWorkItemActivity(Activity activity, IQueueEntry<WorkItemData> entry, Type workItemDataType)
    {
        if (!activity.IsAllDataRequested)
            return;

        activity.AddTag("WorkItemType", entry.Value?.Type);
        activity.AddTag("Id", entry.Id);
        activity.AddTag("CorrelationId", entry.CorrelationId);

        if (entry.Properties is null || entry.Properties.Count <= 0)
            return;

        foreach (var p in entry.Properties)
        {
            if (p.Key != "TraceState")
                activity.AddTag(p.Key, p.Value);
        }
    }

    private readonly ConcurrentDictionary<string, Type> _knownTypesCache = new();
    protected virtual Type? GetWorkItemType(string? workItemType)
    {
        if (String.IsNullOrWhiteSpace(workItemType))
            return null;

        if (_knownTypesCache.TryGetValue(workItemType, out var cachedType))
            return cachedType;

        Type? resolvedType = null;

        try
        {
            resolvedType = Type.GetType(workItemType);
        }
        catch (Exception)
        {
            try
            {
                // try resolve type without version
                string[] typeParts = workItemType.Split(',');
                string shortType = typeParts.Length >= 2
                    ? String.Join(",", typeParts[0], typeParts[1])
                    : workItemType;

                resolvedType = Type.GetType(shortType);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error getting work item type: {WorkItemType}", workItemType);
            }
        }

        if (resolvedType is not null)
            _knownTypesCache.TryAdd(workItemType, resolvedType);

        return resolvedType;
    }

    protected async Task ReportProgressAsync(IWorkItemHandler handler, IQueueEntry<WorkItemData> queueEntry, int progress = 0, string? message = null)
    {
        try
        {
            await _publisher.PublishAsync(new WorkItemStatus
            {
                WorkItemId = queueEntry.Value?.WorkItemId,
                Type = queueEntry.Value?.Type,
                Progress = progress,
                Message = message
            }).AnyContext();
        }
        catch (Exception ex)
        {
            handler.Log.LogError(ex, "Error sending progress report: {Message}", ex.Message);
        }
    }
}

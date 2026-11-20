using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
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

        using var execution = new WorkItemExecution(_publisher, handler, queueEntry, workItemDataType, lockValue, _logger, cancellationToken);
        JobResult processingResult = JobResult.Success;
        bool abandonAttempted = false;
        bool settledByJob = false;
        try
        {
            handler.LogProcessingQueueEntry(queueEntry, workItemDataType, workItemData);
            var context = new WorkItemContext(workItemData, JobId, lockValue, execution, cancellationToken);
            await execution.RunHandlerAsync(context).AnyContext();

            if (queueEntry.IsAbandoned)
            {
                // The queue may have redelivered this entry while the handler was still running.
                processingResult = context.Result.IsSuccess ? JobResult.FailedWithMessage($"Queue entry {queueEntry.Id} was already abandoned") : context.Result;
                return processingResult;
            }

            processingResult = context.Result.IsSuccess ? JobResult.Success : context.Result;
            if (queueEntry.IsCompleted)
                return processingResult;

            abandonAttempted = !processingResult.IsSuccess;
            if (abandonAttempted)
                await CleanupAfterFailureAsync(queueEntry.AbandonAsync, processingResult, queueEntry, handler).AnyContext();
            else
                await queueEntry.CompleteAsync().AnyContext();

            settledByJob = true;
            if (!abandonAttempted)
                handler.LogAutoCompletedQueueEntry(queueEntry, workItemDataType, workItemData);

            return processingResult;
        }
        catch (Exception ex)
        {
            var error = execution.GetProcessingError(ex, activity);
            activity?.SetErrorStatus(error);
            if (abandonAttempted || settledByJob || queueEntry.IsAbandoned || queueEntry.IsCompleted)
            {
                processingResult = JobResult.FromException(error, $"Error processing {queueEntry.Value.Type} work item: {queueEntry.Id} in handler: {workItemDataType.Name}");
                return processingResult;
            }

            processingResult = JobResult.FromException(error, $"Abandoning {queueEntry.Value.Type} work item: {queueEntry.Id}: Error in handler {workItemDataType.Name}");
            abandonAttempted = true;
            await CleanupAfterFailureAsync(queueEntry.AbandonAsync, processingResult, queueEntry, handler).AnyContext();
            settledByJob = true;
            return processingResult;
        }
        finally
        {
            try
            {
                if (processingResult.IsSuccess)
                    await lockValue.ReleaseAsync().AnyContext();
                else
                    await CleanupAfterFailureAsync(lockValue.ReleaseAsync, processingResult, queueEntry, handler).AnyContext();
            }
            finally
            {
                if (queueEntry.Value.SendProgressReports && settledByJob)
                {
                    var report = abandonAttempted
                        ? ReportProgressAsync(handler, queueEntry, -1, $"Failed: {processingResult.Error?.Message ?? processingResult.Message}")
                        : ReportProgressAsync(handler, queueEntry, 100);

                    // A best-effort notification must preserve the queue outcome or release exception.
                    await report.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
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
        activity.AddTag("GroupId", entry.GroupId);

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

    protected Task ReportProgressAsync(IWorkItemHandler handler, IQueueEntry<WorkItemData> queueEntry, int progress = 0, string? message = null)
    {
        return WorkItemExecution.PublishProgressAsync(_publisher, handler, _logger, queueEntry, progress, message);
    }

    private async Task CleanupAfterFailureAsync(Func<Task> cleanup, JobResult failure, IQueueEntry<WorkItemData> entry, IWorkItemHandler handler)
    {
        try
        {
            await cleanup().AnyContext();
        }
        catch (Exception ex)
        {
            try
            {
                var logger = WorkItemExecution.GetLogger(handler, _logger);
                logger.LogError(failure.Error ?? ex, "Cleanup failed for queue entry {QueueEntryId} with {CleanupMessage}, original processing result was {Message}", entry.Id, ex.Message, failure.Message);
            }
            catch (Exception logEx)
            {
                // The caller still needs the provider's cleanup exception if its diagnostic also fails.
                Activity.Current?.AddException(logEx);
            }

            throw;
        }
    }
}

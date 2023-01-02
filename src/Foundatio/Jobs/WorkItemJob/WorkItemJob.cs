using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Foundatio.Messaging;
using Foundatio.Queues;
using Foundatio.Serializer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Foundatio.Jobs {
    [Job(Description = "Processes adhoc work item queues entries")]
    public class WorkItemJob : IQueueJob<WorkItemData>, IHaveLogger {
        protected readonly IMessagePublisher _publisher;
        protected readonly WorkItemHandlers _handlers;
        protected readonly IQueue<WorkItemData> _queue;
        protected readonly ILogger _logger;

        public WorkItemJob(IQueue<WorkItemData> queue, IMessagePublisher publisher, WorkItemHandlers handlers, ILoggerFactory loggerFactory = null) {
            _publisher = publisher;
            _handlers = handlers;
            _queue = queue;
            _logger = loggerFactory?.CreateLogger(GetType()) ?? NullLogger.Instance;
        }

        public string JobId { get; } = Guid.NewGuid().ToString("N").Substring(0, 10);
        IQueue<WorkItemData> IQueueJob<WorkItemData>.Queue => _queue;
        ILogger IHaveLogger.Logger => _logger;

        public async virtual Task<JobResult> RunAsync(CancellationToken cancellationToken = default) {
            IQueueEntry<WorkItemData> queueEntry;

            using (var timeoutCancellationTokenSource = new CancellationTokenSource(30000))
            using (var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellationTokenSource.Token)) {
                try {
                    queueEntry = await _queue.DequeueAsync(linkedCancellationTokenSource.Token).AnyContext();
                } catch (Exception ex) {
                    return JobResult.FromException(ex, $"Error trying to dequeue work item: {ex.Message}");
                }
            }

            return await ProcessAsync(queueEntry, cancellationToken).AnyContext();
        }

        public async Task<JobResult> ProcessAsync(IQueueEntry<WorkItemData> queueEntry, CancellationToken cancellationToken) {
            if (queueEntry == null)
                return JobResult.Success;

            if (cancellationToken.IsCancellationRequested) {
                await queueEntry.AbandonAsync().AnyContext();
                return JobResult.CancelledWithMessage($"Abandoning {queueEntry.Value.Type} work item: {queueEntry.Id}");
            }

            var workItemDataType = GetWorkItemType(queueEntry.Value.Type);
            if (workItemDataType == null) {
                await queueEntry.AbandonAsync().AnyContext();
                return JobResult.FailedWithMessage($"Abandoning {queueEntry.Value.Type} work item: {queueEntry.Id}: Could not resolve work item data type");
            }

            using var activity = StartProcessWorkItemActivity(queueEntry, workItemDataType);
            using var _ = _logger.BeginScope(s => s
                    .Property("JobId", JobId)
                    .Property("QueueEntryId", queueEntry.Id)
                    .PropertyIf("CorrelationId", queueEntry.CorrelationId, !String.IsNullOrEmpty(queueEntry.CorrelationId))
                    .Property("QueueEntryName", workItemDataType.Name));

            object workItemData;
            try {
                workItemData = _queue.Serializer.Deserialize(queueEntry.Value.Data, workItemDataType);
            } catch (Exception ex) {
                await queueEntry.AbandonAsync().AnyContext();
                return JobResult.FromException(ex, $"Abandoning {queueEntry.Value.Type} work item: {queueEntry.Id}: Failed to parse {workItemDataType.Name} work item data");
            }

            var handler = _handlers.GetHandler(workItemDataType);
            if (handler == null) {
                await queueEntry.CompleteAsync().AnyContext();
                return JobResult.FailedWithMessage($"Completing {queueEntry.Value.Type} work item: {queueEntry.Id}: Handler for type {workItemDataType.Name} not registered");
            }

            if (queueEntry.Value.SendProgressReports)
                await ReportProgressAsync(handler, queueEntry).AnyContext();

            var lockValue = await handler.GetWorkItemLockAsync(workItemData, cancellationToken).AnyContext();
            if (lockValue == null) {
                if (handler.Log.IsEnabled(LogLevel.Information))
                    handler.Log.LogInformation("Abandoning {TypeName} work item: {Id}: Unable to acquire work item lock.", queueEntry.Value.Type, queueEntry.Id);

                await queueEntry.AbandonAsync().AnyContext();
                return JobResult.Success;
            }

            var progressCallback = new Func<int, string, Task>(async (progress, message) => {
                if (handler.AutoRenewLockOnProgress) {
                    try {
                        await Task.WhenAll(
                            queueEntry.RenewLockAsync(),
                            lockValue.RenewAsync()
                        ).AnyContext();
                    } catch (Exception ex) {
                        if (handler.Log.IsEnabled(LogLevel.Error))
                            handler.Log.LogError(ex, "Error renewing work item locks: {Message}", ex.Message);
                    }
                }

                await ReportProgressAsync(handler, queueEntry, progress, message).AnyContext();
                if (handler.Log.IsEnabled(LogLevel.Information))
                    handler.Log.LogInformation("{TypeName} Progress {Progress}%: {Message}", workItemDataType.Name, progress, message);
            });

            try {
                handler.LogProcessingQueueEntry(queueEntry, workItemDataType, workItemData);
                var workItemContext = new WorkItemContext(workItemData, JobId, lockValue, cancellationToken, progressCallback);
                await handler.HandleItemAsync(workItemContext).AnyContext();

                if (!workItemContext.Result.IsSuccess) {
                    if (!queueEntry.IsAbandoned && !queueEntry.IsCompleted) {
                        await queueEntry.AbandonAsync().AnyContext();
                        return workItemContext.Result;
                    }
                }

                if (!queueEntry.IsAbandoned && !queueEntry.IsCompleted) {
                    await queueEntry.CompleteAsync().AnyContext();
                    handler.LogAutoCompletedQueueEntry(queueEntry, workItemDataType, workItemData);
                }

                if (queueEntry.Value.SendProgressReports)
                    await ReportProgressAsync(handler, queueEntry, 100).AnyContext();

                return JobResult.Success;
            } catch (Exception ex) {

                await ReportProgressAsync(handler, queueEntry, -1, $"Failed: {ex.Message}").AnyContext();

                if (!queueEntry.IsAbandoned && !queueEntry.IsCompleted) {
                    await queueEntry.AbandonAsync().AnyContext();
                    return JobResult.FromException(ex, $"Abandoning {queueEntry.Value.Type} work item: {queueEntry.Id}: Error in handler {workItemDataType.Name}");
                }

                return JobResult.FromException(ex, $"Error processing {queueEntry.Value.Type} work item: {queueEntry.Id} in handler: {workItemDataType.Name}");
            } finally {
                await lockValue.ReleaseAsync().AnyContext();
            }
        }

        protected virtual Activity StartProcessWorkItemActivity(IQueueEntry<WorkItemData> entry, Type workItemDataType) {
            var activity = FoundatioDiagnostics.ActivitySource.StartActivity("ProcessQueueEntry", ActivityKind.Server, entry.CorrelationId);

            if (activity == null)
                return activity;

            if (entry.Properties != null && entry.Properties.TryGetValue("TraceState", out var traceState))
                activity.TraceStateString = traceState.ToString();

            activity.DisplayName = $"Work Item: {entry.Value.SubMetricName ?? workItemDataType.Name}";

            EnrichProcessWorkItemActivity(activity, entry, workItemDataType);

            return activity;
        }

        protected virtual void EnrichProcessWorkItemActivity(Activity activity, IQueueEntry<WorkItemData> entry, Type workItemDataType) {
            if (!activity.IsAllDataRequested)
                return;

            activity.AddTag("WorkItemType", entry.Value.Type);
            activity.AddTag("Id", entry.Id);
            activity.AddTag("CorrelationId", entry.CorrelationId);

            if (entry.Properties == null || entry.Properties.Count <= 0)
                return;

            foreach (var p in entry.Properties) {
                if (p.Key != "TraceState")
                    activity.AddTag(p.Key, p.Value);
            }
        }

        private readonly ConcurrentDictionary<string, Type> _knownTypesCache = new();
        protected virtual Type GetWorkItemType(string workItemType) {
            return _knownTypesCache.GetOrAdd(workItemType, type => {
                try {
                    return Type.GetType(type);
                } catch (Exception) {
                    try {
                        string[] typeParts = type.Split(',');
                        if (typeParts.Length >= 2)
                            type = String.Join(",", typeParts[0], typeParts[1]);

                        // try resolve type without version
                        return Type.GetType(type);
                    } catch (Exception ex) {
                        if (_logger.IsEnabled(LogLevel.Warning))
                            _logger.LogWarning(ex, "Error getting work item type: {WorkItemType}", type);

                        return null;
                    }
                }
            });
        }

        protected async Task ReportProgressAsync(IWorkItemHandler handler, IQueueEntry<WorkItemData> queueEntry, int progress = 0, string message = null) {
            try {
                await _publisher.PublishAsync(new WorkItemStatus {
                    WorkItemId = queueEntry.Value.WorkItemId,
                    Type = queueEntry.Value.Type,
                    Progress = progress,
                    Message = message
                }).AnyContext();
            } catch (Exception ex) {
                if (handler.Log.IsEnabled(LogLevel.Error))
                    handler.Log.LogError(ex, "Error sending progress report: {Message}", ex.Message);
            }
        }
    }
}

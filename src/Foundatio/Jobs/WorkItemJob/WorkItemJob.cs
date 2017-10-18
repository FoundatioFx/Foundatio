using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Foundatio.Messaging;
using Foundatio.Queues;
using Foundatio.Serializer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

        public async Task<JobResult> RunAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, TimeSpan.FromSeconds(30).ToCancellationToken());

            IQueueEntry<WorkItemData> queueEntry;
            try {
                queueEntry = await _queue.DequeueAsync(linkedCancellationTokenSource.Token).AnyContext();
            } catch (Exception ex) {
                return JobResult.FromException(ex, $"Error trying to dequeue work item: {ex.Message}");
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

            Type workItemDataType;
            try {
                workItemDataType = Type.GetType(queueEntry.Value.Type);
            } catch (Exception ex) {
                await queueEntry.AbandonAsync().AnyContext();
                return JobResult.FromException(ex, $"Abandoning {queueEntry.Value.Type} work item: {queueEntry.Id}: Could not resolve work item data type.");
            }

            if (workItemDataType == null) {
                await queueEntry.AbandonAsync().AnyContext();
                return JobResult.FailedWithMessage($"Abandoning {queueEntry.Value.Type} work item: {queueEntry.Id}: Could not resolve work item data type.");
            }

            object workItemData;
            try {
                workItemData = _queue.Serializer.Deserialize(queueEntry.Value.Data, workItemDataType);
            } catch (Exception ex) {
                await queueEntry.AbandonAsync().AnyContext();
                return JobResult.FromException(ex, $"Abandoning {queueEntry.Value.Type} work item: {queueEntry.Id}: Failed to parse {workItemDataType.Name} work item data.");
            }

            var handler = _handlers.GetHandler(workItemDataType);
            if (handler == null) {
                await queueEntry.CompleteAsync().AnyContext();
                return JobResult.FailedWithMessage($"Completing {queueEntry.Value.Type} work item: {queueEntry.Id}: Handler for type {workItemDataType.Name} not registered.");
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
                await handler.HandleItemAsync(new WorkItemContext(workItemData, JobId, lockValue, cancellationToken, progressCallback)).AnyContext();

                if (!queueEntry.IsAbandoned && !queueEntry.IsCompleted) {
                    await queueEntry.CompleteAsync().AnyContext();
                    handler.LogAutoCompletedQueueEntry(queueEntry, workItemDataType, workItemData);
                }

                if (queueEntry.Value.SendProgressReports)
                    await ReportProgressAsync(handler, queueEntry, 100).AnyContext();

                return JobResult.Success;
            } catch (Exception ex) {
                if (!queueEntry.IsAbandoned && !queueEntry.IsCompleted) {
                    await queueEntry.AbandonAsync().AnyContext();
                    return JobResult.FromException(ex, $"Abandoning {queueEntry.Value.Type} work item: {queueEntry.Id}: Error in handler {workItemDataType.Name}.");
                }

                return JobResult.FromException(ex, $"Error processing {queueEntry.Value.Type} work item: {queueEntry.Id} in handler: {workItemDataType.Name}");
            } finally {
                await lockValue.ReleaseAsync().AnyContext();
            }
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

using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Messaging;
using Foundatio.Queues;
using Foundatio.Serializer;

namespace Foundatio.Jobs {
    public class WorkItemJob : IQueueJob, IHaveLogger {
        protected readonly IMessageBus _messageBus;
        protected readonly WorkItemHandlers _handlers;
        protected readonly IQueue<WorkItemData> _queue;
        protected readonly ILogger _logger;

        public WorkItemJob(IQueue<WorkItemData> queue, IMessageBus messageBus, WorkItemHandlers handlers, ILoggerFactory loggerFactory = null) {
            _messageBus = messageBus;
            _handlers = handlers;
            _queue = queue;
            _logger = loggerFactory.CreateLogger(GetType());
        }

        public string JobId { get; } = Guid.NewGuid().ToString("N").Substring(0, 10);
        IQueue IQueueJob.Queue => _queue;
        ILogger IHaveLogger.Logger => _logger;

        public async Task<JobResult> RunAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, TimeSpan.FromSeconds(30).ToCancellationToken());

            IQueueEntry<WorkItemData> queueEntry;
            try {
                queueEntry = await _queue.DequeueAsync(linkedCancellationTokenSource.Token).AnyContext();
            } catch (Exception ex) {
                return JobResult.FromException(ex, $"Error trying to dequeue work item: {ex.Message}");
            }

            if (queueEntry == null)
                return JobResult.Success;

            if (cancellationToken.IsCancellationRequested) {
                _logger.Info("Job was cancelled. Abandoning {queueEntryType} work item: {queueEntryId}", queueEntry.Value.Type, queueEntry.Id);
                await queueEntry.AbandonAsync().AnyContext();
                return JobResult.CancelledWithMessage($"Abandoning {queueEntry.Value.Type} work item: {queueEntry.Id}");
            }

            Type workItemDataType = null;
            try {
                workItemDataType = Type.GetType(queueEntry.Value.Type);
            } catch (Exception ex) {
                await queueEntry.AbandonAsync().AnyContext();
                return JobResult.FromException(ex, $"Could not resolve {workItemDataType.Name} work item data type.");
            }

            if (workItemDataType == null) {
                await queueEntry.AbandonAsync().AnyContext();
                return JobResult.FailedWithMessage($"Could not resolve {workItemDataType.Name} work item data type.");
            }

            object workItemData;
            try {
                workItemData = await _queue.Serializer.DeserializeAsync(queueEntry.Value.Data, workItemDataType).AnyContext();
            } catch (Exception ex) {
                await queueEntry.AbandonAsync().AnyContext();
                return JobResult.FromException(ex, $"Failed to parse {workItemDataType.Name} work item data.");
            }

            var handler = _handlers.GetHandler(workItemDataType);
            if (handler == null) {
                await queueEntry.CompleteAsync().AnyContext();
                return JobResult.FailedWithMessage($"Handler for type {workItemDataType.Name} not registered.");
            }

            if (queueEntry.Value.SendProgressReports)
                await _messageBus.PublishAsync(new WorkItemStatus {
                    WorkItemId = queueEntry.Value.WorkItemId,
                    Progress = 0,
                    Type = queueEntry.Value.Type
                }).AnyContext();

            var lockValue = await handler.GetWorkItemLockAsync(workItemData, cancellationToken).AnyContext();
            if (lockValue == null) {
                await queueEntry.AbandonAsync().AnyContext();
                handler.Log.Trace("Unable to acquire work item lock.");
                return JobResult.Success;
            }

            var progressCallback = new Func<int, string, Task>(async (progress, message) => {
                if (handler.AutoRenewLockOnProgress)
                    await queueEntry.RenewLockAsync().AnyContext();

                if (handler.AutoRenewLockOnProgress)
                    await lockValue.RenewAsync().AnyContext();

                await _messageBus.PublishAsync(new WorkItemStatus {
                    WorkItemId = queueEntry.Value.WorkItemId,
                    Progress = progress,
                    Message = message,
                    Type = queueEntry.Value.Type
                }).AnyContext();
            });

            try {
                handler.LogProcessingQueueEntry(queueEntry, workItemDataType, workItemData);
                await handler.HandleItemAsync(new WorkItemContext(workItemData, JobId, lockValue, cancellationToken, progressCallback)).AnyContext();

                if (!queueEntry.IsAbandoned && !queueEntry.IsCompleted) {
                    await queueEntry.CompleteAsync().AnyContext();
                    handler.LogAutoCompletedQueueEntry(queueEntry, workItemDataType, workItemData);
                }

                if (queueEntry.Value.SendProgressReports)
                    await _messageBus.PublishAsync(new WorkItemStatus {
                        WorkItemId = queueEntry.Value.WorkItemId,
                        Progress = 100,
                        Type = queueEntry.Value.Type
                    }).AnyContext();

                return JobResult.Success;
            } catch (Exception ex) {
                if (!queueEntry.IsAbandoned && !queueEntry.IsCompleted)
                    await queueEntry.AbandonAsync().AnyContext();

                handler.Log.Error(ex, "Error processing {0} work item queue entry ({1}).", workItemDataType.Name, queueEntry.Id);
                return JobResult.FromException(ex, $"Error in handler {workItemDataType.Name}.");
            } finally {
                await lockValue.ReleaseAsync().AnyContext();
            }
        }
    }
}

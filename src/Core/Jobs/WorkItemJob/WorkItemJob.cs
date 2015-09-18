using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Messaging;
using Foundatio.Queues;
using Foundatio.Serializer;

namespace Foundatio.Jobs {
    public class WorkItemJob : QueueProcessorJobBase<WorkItemData> {
        protected readonly IMessageBus _messageBus;
        protected readonly WorkItemHandlers _handlers;

        public WorkItemJob(IQueue<WorkItemData> queue, IMessageBus messageBus, WorkItemHandlers handlers) : base(queue) {
            _messageBus = messageBus;
            _handlers = handlers;
            AutoComplete = false;
        }

        protected async override Task<JobResult> ProcessQueueItemAsync(QueueEntry<WorkItemData> queueEntry, CancellationToken cancellationToken) {
            var workItemDataType = Type.GetType(queueEntry.Value.Type);
            if (workItemDataType == null)
                return JobResult.FailedWithMessage("Could not resolve work item data type.");

            object workItemData;
            try {
                workItemData = await _queue.Serializer.DeserializeAsync(queueEntry.Value.Data, workItemDataType).AnyContext();
            } catch (Exception ex) {
                return JobResult.FromException(ex, "Failed to parse work item data.");
            }

            var handler = _handlers.GetHandler(workItemDataType);
            if (handler == null)
                return JobResult.FailedWithMessage("Handler for type {0} not registered.", workItemDataType.Name);

            var progressCallback = new Func<int, string, Task>(async (progress, message) => await _messageBus.PublishAsync(new WorkItemStatus {
                WorkItemId = queueEntry.Value.WorkItemId,
                Progress = progress,
                Message = message,
                Type = queueEntry.Value.Type
            }).AnyContext());

            if (queueEntry.Value.SendProgressReports)
                await _messageBus.PublishAsync(new WorkItemStatus {
                    WorkItemId = queueEntry.Value.WorkItemId,
                    Progress = 0,
                    Type = queueEntry.Value.Type
                }).AnyContext();

            var ctx = new WorkItemContext(workItemData, JobId, progressCallback);
            using (var lockValue = await handler.GetWorkItemLockAsync(ctx, cancellationToken).AnyContext()) {
                if (lockValue == null)
                    return JobResult.SuccessWithMessage("Unable to acquire work item lock.");
                
				try {
                    await handler.HandleItemAsync(ctx, cancellationToken).AnyContext();
                } catch (Exception ex) {
                    await queueEntry.AbandonAsync().AnyContext();
                    return JobResult.FromException(ex, "Error in handler {0}.", workItemDataType.Name);
                }

                await queueEntry.CompleteAsync().AnyContext();
                if (queueEntry.Value.SendProgressReports)
                    await _messageBus.PublishAsync(new WorkItemStatus {
                        WorkItemId = queueEntry.Value.WorkItemId,
                        Progress = 100,
                        Type = queueEntry.Value.Type
                    }).AnyContext();

                return JobResult.Success;
            }
        }
    }
}

using System;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Queues;
using Foundatio.Serializer;

namespace Foundatio.Jobs {
    public class WorkItemJob : QueueProcessorJobBase<WorkItemData> {
        protected readonly IMessageBus _messageBus;
        protected readonly WorkItemHandlers _handlers;

        public WorkItemJob(IQueue<WorkItemData> queue, IMessageBus messageBus, WorkItemHandlers handlers)
            : base(queue) {
            _messageBus = messageBus;
            _handlers = handlers;
        }

        protected async override Task<JobResult> ProcessQueueItem(QueueEntry<WorkItemData> queueEntry) {
            var workItemDataType = Type.GetType(queueEntry.Value.Type);
            if (workItemDataType == null)
                return JobResult.FailedWithMessage("Could not resolve work item data type.");

            object workItemData;
            try {
                workItemData = _queue.Serializer.Deserialize(queueEntry.Value.Data, workItemDataType);
            } catch (Exception ex) {
                return JobResult.FromException(ex, "Failed to parse work item data.");
            }

            var handler = _handlers.GetHandler(workItemDataType);
            if (handler == null)
                return JobResult.FailedWithMessage("Handler for type {0} not registered.", workItemDataType.Name);

            var progressCallback = new Action<int, string>((progress, message) => _messageBus.Publish(new WorkItemStatus {
                WorkItemId = queueEntry.Value.WorkItemId,
                Progress = progress,
                Message = message
            }));

            _messageBus.Publish(new WorkItemStatus { WorkItemId = queueEntry.Value.WorkItemId, Progress = 0 });
            try {
                await handler.HandleItem(new WorkItemContext(workItemData, progressCallback));
            } catch (Exception ex) {
                return JobResult.FromException(ex, "Error in handler {0}.", workItemDataType.Name);
            }

            queueEntry.Complete();
            _messageBus.Publish(new WorkItemStatus { WorkItemId = queueEntry.Value.WorkItemId, Progress = 100 });

            return JobResult.Success;
        }
    }
}

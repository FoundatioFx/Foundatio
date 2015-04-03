using System;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Queues;
using Foundatio.Serializer;

namespace Foundatio.Jobs {
    public class WorkItemJob : QueueProcessorJobBase<WorkItemData> {
        protected readonly IMessagePublisher _publisher;
        protected readonly WorkItemHandlers Handlers;

        public WorkItemJob(IQueue<WorkItemData> queue, IMessagePublisher publisher, WorkItemHandlers handlers)
            : base(queue) {
            _publisher = publisher;
            Handlers = handlers;
        }

        protected async override Task<JobResult> ProcessQueueItem(QueueEntry<WorkItemData> queueEntry) {
            var workItemDataType = Type.GetType(queueEntry.Value.Type);
            if (workItemDataType == null)
                return JobResult.FailedWithMessage("Could not resolve work item data type.");

            var workItemData = _queue.Serializer.Deserialize(queueEntry.Value.Data, workItemDataType);

            var handler = Handlers.GetHandler(workItemDataType);
            var progressCallback = new Action<int, string>((progress, message) => _publisher.Publish(new WorkItemStatus {
                TaskId = queueEntry.Value.WorkItemId,
                Progress = progress,
                Message = message
            }));
            handler(new WorkItemContext(workItemData, progressCallback));
            
            queueEntry.Complete();
            _publisher.Publish(new WorkItemStatus { TaskId = queueEntry.Value.WorkItemId, Progress = 100 });

            return JobResult.Success;
        }
    }
}

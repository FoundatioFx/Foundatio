using System;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Queues;
using Foundatio.Serializer;

namespace Foundatio.Jobs {
    public class LongRunningTasksJob : QueueProcessorJobBase<LongRunningTaskData> {
        protected readonly IMessagePublisher _publisher;
        protected readonly LongRunningTaskHandlerRegistry _handlerRegistry;

        public LongRunningTasksJob(IQueue<LongRunningTaskData> queue, IMessagePublisher publisher, LongRunningTaskHandlerRegistry handlerRegistry)
            : base(queue) {
            _publisher = publisher;
            _handlerRegistry = handlerRegistry;
        }

        protected async override Task<JobResult> ProcessQueueItem(QueueEntry<LongRunningTaskData> queueEntry) {
            var jobDataType = Type.GetType(queueEntry.Value.Type);
            if (jobDataType == null)
                return JobResult.FailedWithMessage("Could not load job data type.");

            var jobData = _queue.Serializer.Deserialize(queueEntry.Value.Data, jobDataType);

            var handler = _handlerRegistry.GetHandler(jobDataType);
            var progressCallback = new Action<int, string>((progress, message) => _publisher.Publish(new LongRunningTaskStatus {
                JobId = queueEntry.Value.JobId,
                Progress = progress,
                Message = message
            }));
            handler(new LongRunningTaskContext(jobData, progressCallback));

            queueEntry.Complete();
            _publisher.Publish(new LongRunningTaskStatus { JobId = queueEntry.Value.JobId, Progress = 100 });

            return JobResult.Success;
        }
    }
}

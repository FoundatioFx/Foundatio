using System;
using System.Threading.Tasks;
using Exceptionless;
using Foundatio.Jobs;
using Foundatio.Queues;

namespace Foundatio.JobSample.Jobs {
    public class PingQueueJob : QueueProcessorJobBase<PingRequest> {
        public PingQueueJob(IQueue<PingRequest> queue)
            : base(queue) {
        }

        public int RunCount { get; set; }

        protected override Task<JobResult> ProcessQueueItem(QueueEntry<PingRequest> queueEntry) {
            RunCount++;

            Console.WriteLine("Pong!");

            if (RandomData.GetBool(80))
                queueEntry.Complete();
            else if (RandomData.GetBool(80))
                queueEntry.Abandon();

            return Task.FromResult(JobResult.Success);
        }
    }

    public class PingRequest {
        public string Data { get; set; }
    }
}

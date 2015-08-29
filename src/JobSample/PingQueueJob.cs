using System;
using System.Threading.Tasks;
using Exceptionless;
using Foundatio.Extensions;
using Foundatio.Jobs;
using Foundatio.Queues;

namespace Foundatio.JobSample.Jobs {
    public class PingQueueJob : QueueProcessorJobBase<PingRequest> {
        public PingQueueJob(IQueue<PingRequest> queue)
            : base(queue) {
        }

        public int RunCount { get; set; }

        protected override async Task<JobResult> ProcessQueueItemAsync(QueueEntry<PingRequest> queueEntry) {
            RunCount++;

            Console.WriteLine("Pong!");

            if (RandomData.GetBool(80))
                await queueEntry.CompleteAsync().AnyContext();
            else if (RandomData.GetBool(80))
                await queueEntry.AbandonAsync().AnyContext();

            return JobResult.Success;
        }
    }

    public class PingRequest {
        public string Data { get; set; }
    }
}

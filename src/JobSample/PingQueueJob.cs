using System;
using System.Threading.Tasks;
using Exceptionless;
using Foundatio.Extensions;
using Foundatio.Jobs;
using Foundatio.Queues;

namespace Foundatio.JobSample.Jobs {
    public class PingQueueJob : QueueProcessorJobBase<PingRequest> {
        public PingQueueJob(IQueue<PingRequest> queue) : base(queue) {
            AutoComplete = false;
        }

        public int RunCount { get; set; }

        protected override async Task<JobResult> ProcessQueueEntryAsync(JobQueueEntryContext<PingRequest> context) {
            RunCount++;

            Console.WriteLine("Pong!");

            if (RandomData.GetBool(80))
                await context.QueueEntry.CompleteAsync().AnyContext();
            else if (RandomData.GetBool(80))
                await context.QueueEntry.AbandonAsync().AnyContext();

            return JobResult.Success;
        }
    }

    public class PingRequest {
        public string Data { get; set; }
    }
}

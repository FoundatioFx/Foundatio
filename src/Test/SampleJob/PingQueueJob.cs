using System;
using System.Threading.Tasks;
using Exceptionless;
using Foundatio.Extensions;
using Foundatio.Jobs;
using Foundatio.Queues;
using Foundatio.Logging;

namespace Foundatio.SampleJob.Jobs {
    public class PingQueueJob : QueueProcessorJobBase<PingRequest> {
        public PingQueueJob(IQueue<PingRequest> queue, ILoggerFactory loggerFactory) : base(queue, loggerFactory) {
            AutoComplete = true;
        }

        public int RunCount { get; set; }

        protected override Task<JobResult> ProcessQueueEntryAsync(JobQueueEntryContext<PingRequest> context) {
            RunCount++;

            _logger.Info(() => $"Got {RunCount.ToOrdinal()} ping. Sending pong!");

            if (RandomData.GetBool(context.QueueEntry.Value.PercentChanceOfException))
                throw new ApplicationException("Boom!");

            return Task.FromResult(JobResult.Success);
        }
    }

    public class PingRequest {
        public string Data { get; set; }
        public int PercentChanceOfException { get; set; } = 0;
    }
}

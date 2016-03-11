using System;
using System.Threading.Tasks;
using Exceptionless;
using Foundatio.Extensions;
using Foundatio.Jobs;
using Foundatio.Queues;
using Foundatio.Logging;

namespace Foundatio.SampleJob.Jobs {
    public class PingQueueJob : QueueJobBase<PingRequest> {
        public PingQueueJob(IQueue<PingRequest> queue, ILoggerFactory loggerFactory) : base(queue, loggerFactory) {
            AutoComplete = true;
        }

        public int RunCount { get; set; }
        
        protected override async Task<JobResult> ProcessQueueEntryAsync(QueueEntryContext<PingRequest> context) {
            RunCount++;

            _logger.Info(() => $"Got {RunCount.ToOrdinal()} ping. Sending pong!");

            //await Task.Delay(TimeSpan.FromSeconds(10)).AnyContext();

            if (RandomData.GetBool(context.QueueEntry.Value.PercentChanceOfException))
                throw new ApplicationException("Boom!");

            return JobResult.Success;
        }
    }

    public class PingRequest {
        public string Data { get; set; }
        public int PercentChanceOfException { get; set; } = 0;
    }
}

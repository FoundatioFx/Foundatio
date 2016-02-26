using System;
using System.Threading.Tasks;
using Exceptionless;
using Foundatio.Extensions;
using Foundatio.Jobs;
using Foundatio.Queues;
using Foundatio.Logging;

namespace Foundatio.JobSample.Jobs {
    public class PingQueueJob : QueueProcessorJobBase<PingRequest> {
        public PingQueueJob(IQueue<PingRequest> queue, ILoggerFactory loggerFactory) : base(queue, loggerFactory) {
            AutoComplete = false;
        }

        public int RunCount { get; set; }

        protected override Task<JobResult> ProcessQueueEntryAsync(JobQueueEntryContext<PingRequest> context) {
            RunCount++;

            _logger.Info(() => $"Got {RunCount.ToOrdinal()} ping. Sending pong!");

            if (RandomData.GetBool(1))
                throw new ApplicationException("Boom!");

            return Task.FromResult(JobResult.Success);
        }
    }

    public class PingRequest {
        public string Data { get; set; }
    }

    public class EnqueuePings : IStartupAction {
        private readonly IQueue<PingRequest> _pingQueue;
        private readonly ILogger _logger;

        public EnqueuePings(IQueue<PingRequest> pingQueue, ILogger<EnqueuePings> logger) {
            _pingQueue = pingQueue;
            _logger = logger;
        }

        public async Task RunAsync() {
            var startDate = DateTime.Now;
            while (startDate.AddSeconds(30) > DateTime.Now) {
                _logger.Info("Enqueueing ping.");
                await _pingQueue.EnqueueAsync(new PingRequest { Data = "Hi" }).AnyContext();
                await Task.Delay(RandomData.GetInt(100, 1000)).AnyContext();
            }
        }
    }
}

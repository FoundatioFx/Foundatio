using System;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Metrics;
using Foundatio.Queues;
using Foundatio.Tests.Utility;

namespace Foundatio.Tests.Jobs {
    public class SampleQueueJob : QueueProcessorJobBase<SampleQueueWorkItem> {
        private readonly IMetricsClient _metrics;

        public SampleQueueJob(IQueue<SampleQueueWorkItem> queue, IMetricsClient metrics) : base(queue) {
            _metrics = metrics;
        }

        protected override Task<JobResult> ProcessQueueItem(QueueEntry<SampleQueueWorkItem> queueEntry) {
            _metrics.Counter("dequeued");

            if (RandomData.GetBool(10)) {
                _metrics.Counter("errors");
                throw new ApplicationException("Boom!");
            }

            if (RandomData.GetBool(10)) {
                _metrics.Counter("abandoned");
                queueEntry.Abandon();
                return Task.FromResult(JobResult.FailedWithMessage("Abandoned"));
            }

            queueEntry.Complete();
            _metrics.Counter("completed");

            return Task.FromResult(JobResult.Success);
        }
    }

    public class SampleQueueWorkItem {
        public string Path { get; set; }
        public DateTime Created { get; set; }
    }
}

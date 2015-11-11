using System;
using System.Threading.Tasks;
using Foundatio.Extensions;
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
        
        protected override async Task<JobResult> ProcessQueueEntryAsync(JobQueueEntryContext<SampleQueueWorkItem> context) {
            await _metrics.CounterAsync("dequeued").AnyContext();

            if (RandomData.GetBool(10)) {
                await _metrics.CounterAsync("errors").AnyContext();
                throw new ApplicationException("Boom!");
            }

            if (RandomData.GetBool(10)) {
                await _metrics.CounterAsync("abandoned").AnyContext();
                return JobResult.FailedWithMessage("Abandoned");
            }
            
            await _metrics.CounterAsync("completed").AnyContext();
            return JobResult.Success;
        }
    }

    public class SampleQueueWorkItem {
        public string Path { get; set; }
        public DateTime Created { get; set; }
    }

    public class SampleJob : JobBase {
        private readonly IMetricsClient _metrics;

        public SampleJob(IMetricsClient metrics) {
            _metrics = metrics;
        }

        protected override async Task<JobResult> RunInternalAsync(JobRunContext context) {
            await _metrics.CounterAsync("runs").AnyContext();

            if (RandomData.GetBool(10)) {
                await _metrics.CounterAsync("errors").AnyContext();
                throw new ApplicationException("Boom!");
            }

            if (RandomData.GetBool(10)) {
                await _metrics.CounterAsync("failed").AnyContext();
                return JobResult.FailedWithMessage("Failed");
            }

            await _metrics.CounterAsync("completed").AnyContext();
            return JobResult.Success;
        }
    }
}
using System;
using System.Threading;
using System.Threading.Tasks;
using Exceptionless;
using Foundatio.Jobs;
using Foundatio.Lock;
using Foundatio.Metrics;
using Foundatio.Queues;
using Microsoft.Extensions.Logging;

namespace Foundatio.Tests.Jobs {
    public class SampleQueueJob : QueueJobBase<SampleQueueWorkItem> {
        private readonly IMetricsClient _metrics;

        public SampleQueueJob(IQueue<SampleQueueWorkItem> queue, IMetricsClient metrics, ILoggerFactory loggerFactory = null) : base(queue, loggerFactory) {
            _metrics = metrics ?? NullMetricsClient.Instance;
        }

        protected override Task<JobResult> ProcessQueueEntryAsync(QueueEntryContext<SampleQueueWorkItem> context) {
            _metrics.Counter("dequeued");

            if (RandomData.GetBool(10)) {
                _metrics.Counter("errors");
                throw new Exception("Boom!");
            }

            if (RandomData.GetBool(10)) {
                _metrics.Counter("abandoned");
                return Task.FromResult(JobResult.FailedWithMessage("Abandoned"));
            }
            
            _metrics.Counter("completed");
            return Task.FromResult(JobResult.Success);
        }
    }

    public class SampleQueueJobWithLocking : QueueJobBase<SampleQueueWorkItem> {
        private readonly IMetricsClient _metrics;
        private readonly ILockProvider _lockProvider;

        public SampleQueueJobWithLocking(IQueue<SampleQueueWorkItem> queue, IMetricsClient metrics, ILockProvider lockProvider, ILoggerFactory loggerFactory = null) : base(queue, loggerFactory) {
            _metrics = metrics ?? NullMetricsClient.Instance;
            _lockProvider = lockProvider;
        }

        protected override Task<ILock> GetQueueEntryLockAsync(IQueueEntry<SampleQueueWorkItem> queueEntry, CancellationToken cancellationToken = new CancellationToken()) {
            if (_lockProvider != null)
                return _lockProvider.AcquireAsync("job", TimeSpan.FromMilliseconds(100), TimeSpan.Zero);

            return base.GetQueueEntryLockAsync(queueEntry, cancellationToken);
        }

        protected override Task<JobResult> ProcessQueueEntryAsync(QueueEntryContext<SampleQueueWorkItem> context) {
            _metrics.Counter("completed");
            return Task.FromResult(JobResult.Success);
        }
    }

    public class SampleQueueWorkItem {
        public string Path { get; set; }
        public DateTime Created { get; set; }
    }

    public class SampleJob : JobBase {
        private readonly IMetricsClient _metrics;

        public SampleJob(IMetricsClient metrics, ILoggerFactory loggerFactory) : base(loggerFactory) {
            _metrics = metrics;
        }

        protected override Task<JobResult> RunInternalAsync(JobContext context) {
            _metrics.Counter("runs");

            if (RandomData.GetBool(10)) {
                _metrics.Counter("errors");
                throw new Exception("Boom!");
            }

            if (RandomData.GetBool(10)) {
                _metrics.Counter("failed");
                return Task.FromResult(JobResult.FailedWithMessage("Failed"));
            }

            _metrics.Counter("completed");
            return Task.FromResult(JobResult.Success);
        }
    }
}
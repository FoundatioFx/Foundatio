using System;
using System.Net.Http;
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
        private readonly HttpClient _httpClient;

        public SampleQueueJob(IQueue<SampleQueueWorkItem> queue, IMetricsClient metrics, ILoggerFactory loggerFactory = null) : base(queue, loggerFactory) {
            _metrics = metrics ?? NullMetricsClient.Instance;
            _httpClient = new HttpClient();
        }

        protected override async Task<JobResult> ProcessQueueEntryAsync(QueueEntryContext<SampleQueueWorkItem> context) {
            _metrics.Counter("dequeued");

            if (context.QueueEntry.Value.ShouldFail) {
                //await Task.Delay(TimeSpan.FromSeconds(1));
                await _httpClient.PostAsync("http://localhost", new StringContent("test"));
            }

            if (RandomData.GetBool(10)) {
                _metrics.Counter("errors");
                throw new Exception("Boom!");
            }

            if (RandomData.GetBool(10)) {
                _metrics.Counter("abandoned");
                return JobResult.FailedWithMessage("Abandoned");
            }
            
            _metrics.Counter("completed");
            return JobResult.Success;
        }
    }

    public class SampleQueueJobWithLocking : QueueJobBase<SampleQueueWorkItem> {
        private readonly IMetricsClient _metrics;
        private readonly ILockProvider _lockProvider;
        private readonly HttpClient _httpClient;

        public SampleQueueJobWithLocking(IQueue<SampleQueueWorkItem> queue, IMetricsClient metrics, ILockProvider lockProvider, ILoggerFactory loggerFactory = null) : base(queue, loggerFactory) {
            _metrics = metrics ?? NullMetricsClient.Instance;
            _lockProvider = lockProvider;
            _httpClient = new HttpClient();
        }

        protected override Task<ILock> GetQueueEntryLockAsync(IQueueEntry<SampleQueueWorkItem> queueEntry, CancellationToken cancellationToken = default(CancellationToken)) {
            if (_lockProvider != null)
                return _lockProvider.AcquireAsync("job", TimeSpan.FromMilliseconds(100), TimeSpan.Zero);

            return base.GetQueueEntryLockAsync(queueEntry, cancellationToken);
        }

        protected override async Task<JobResult> ProcessQueueEntryAsync(QueueEntryContext<SampleQueueWorkItem> context) {
            _metrics.Counter("runs");
            if (context.QueueEntry.Value.ShouldFail)
                await _httpClient.PostAsync("http://localhost", new StringContent("test"));
            
            _metrics.Counter("completed");
            return JobResult.Success;
        }
    }

    public class SampleQueueWorkItem {
        public string Path { get; set; }
        public DateTime Created { get; set; }
        public bool ShouldFail { get; set; }
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
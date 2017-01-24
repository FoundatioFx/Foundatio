﻿using System;
using System.Threading;
using System.Threading.Tasks;
using Exceptionless;
using Foundatio.Extensions;
using Foundatio.Jobs;
using Foundatio.Lock;
using Foundatio.Metrics;
using Foundatio.Queues;
using Foundatio.Logging;

namespace Foundatio.Tests.Jobs {
    public class SampleQueueJob : QueueJobBase<SampleQueueWorkItem> {
        private readonly IMetricsClient _metrics;

        public SampleQueueJob(IQueue<SampleQueueWorkItem> queue, IMetricsClient metrics, ILoggerFactory loggerFactory = null) : base(queue, loggerFactory) {
            _metrics = metrics ?? NullMetricsClient.Instance;
        }

        protected override async Task<JobResult> ProcessQueueEntryAsync(QueueEntryContext<SampleQueueWorkItem> context) {
            await _metrics.CounterAsync("dequeued");

            if (RandomData.GetBool(10)) {
                await _metrics.CounterAsync("errors");
                throw new Exception("Boom!");
            }

            if (RandomData.GetBool(10)) {
                await _metrics.CounterAsync("abandoned");
                return JobResult.FailedWithMessage("Abandoned");
            }
            
            await _metrics.CounterAsync("completed");
            return JobResult.Success;
        }
    }

    public class SampleQueueJobWithLocking : QueueJobBase<SampleQueueWorkItem> {
        private readonly IMetricsClient _metrics;
        private readonly ILockProvider _lockProvider;

        public SampleQueueJobWithLocking(IQueue<SampleQueueWorkItem> queue, IMetricsClient metrics, ILockProvider lockProvider, ILoggerFactory loggerFactory = null) : base(queue, loggerFactory) {
            _metrics = metrics ?? NullMetricsClient.Instance;
            _lockProvider = lockProvider;
        }

        protected override async Task<ILock> GetQueueEntryLockAsync(IQueueEntry<SampleQueueWorkItem> queueEntry, CancellationToken cancellationToken = new CancellationToken()) {
            if (_lockProvider != null)
                return await _lockProvider.AcquireAsync("job", TimeSpan.FromMilliseconds(100), TimeSpan.Zero);

            return await base.GetQueueEntryLockAsync(queueEntry, cancellationToken);
        }

        protected override async Task<JobResult> ProcessQueueEntryAsync(QueueEntryContext<SampleQueueWorkItem> context) {
            await _metrics.CounterAsync("completed");
            return JobResult.Success;
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

        protected override async Task<JobResult> RunInternalAsync(JobContext context) {
            await _metrics.CounterAsync("runs");

            if (RandomData.GetBool(10)) {
                await _metrics.CounterAsync("errors");
                throw new Exception("Boom!");
            }

            if (RandomData.GetBool(10)) {
                await _metrics.CounterAsync("failed");
                return JobResult.FailedWithMessage("Failed");
            }

            await _metrics.CounterAsync("completed");
            return JobResult.Success;
        }
    }
}
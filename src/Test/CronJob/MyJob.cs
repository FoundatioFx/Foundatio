using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Logging;

namespace Foundatio.CronJob {
    public class EveryMinuteJob : IJob {
        private readonly ILogger _logger;

        public EveryMinuteJob(ILoggerFactory loggerFactory) {
            _logger = loggerFactory.CreateLogger<EveryMinuteJob>();
        }

        public Task<JobResult> RunAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            _logger.Info($"EveryMinuteJob Run {Thread.CurrentThread.ManagedThreadId}");
            return Task.FromResult(JobResult.Success);
        }
    }

    public class EvenMinuteJob : IJob {
        private readonly ILogger _logger;

        public EvenMinuteJob(ILoggerFactory loggerFactory) {
            _logger = loggerFactory.CreateLogger<EvenMinuteJob>();
        }

        public Task<JobResult> RunAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            _logger.Info($"EvenMinuteJob Run {Thread.CurrentThread.ManagedThreadId}");
            return Task.FromResult(JobResult.Success);
        }
    }
}

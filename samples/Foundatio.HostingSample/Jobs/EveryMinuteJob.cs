using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Microsoft.Extensions.Logging;

namespace Foundatio.HostingSample {
    public class EveryMinuteJob : IJob {
        private readonly ILogger _logger;

        public EveryMinuteJob(ILoggerFactory loggerFactory) {
            _logger = loggerFactory.CreateLogger<EveryMinuteJob>();
        }

        public Task<JobResult> RunAsync(CancellationToken cancellationToken = default) {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("EveryMinuteJob Run {ManagedThreadId}", Thread.CurrentThread.ManagedThreadId);

            return Task.FromResult(JobResult.Success);
        }
    }
}

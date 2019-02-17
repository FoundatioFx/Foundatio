using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Microsoft.Extensions.Logging;

namespace Foundatio.HostingSample {
    public class EvenMinuteJob : IJob {
        private readonly ILogger _logger;

        public EvenMinuteJob(ILoggerFactory loggerFactory) {
            _logger = loggerFactory.CreateLogger<EvenMinuteJob>();
        }

        public Task<JobResult> RunAsync(CancellationToken cancellationToken = default) {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("EvenMinuteJob Run {ManagedThreadId}", Thread.CurrentThread.ManagedThreadId);

            return Task.FromResult(JobResult.Success);
        }
    }
}

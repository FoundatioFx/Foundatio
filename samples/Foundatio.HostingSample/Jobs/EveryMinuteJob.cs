using System;
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

        public async Task<JobResult> RunAsync(CancellationToken cancellationToken = default) {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("EveryMinuteJob Run Thread={ManagedThreadId}", Thread.CurrentThread.ManagedThreadId);

            await Task.Delay(TimeSpan.FromSeconds(4));

            return JobResult.Success;
        }
    }
}

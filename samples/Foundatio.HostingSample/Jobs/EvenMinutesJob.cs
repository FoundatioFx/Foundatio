using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Microsoft.Extensions.Logging;

namespace Foundatio.HostingSample
{
    public class EvenMinutesJob : IJob
    {
        private readonly ILogger _logger;

        public EvenMinutesJob(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<EvenMinutesJob>();
        }

        public async Task<JobResult> RunAsync(CancellationToken cancellationToken = default)
        {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("EvenMinuteJob Run Thread={ManagedThreadId}", Thread.CurrentThread.ManagedThreadId);

            await Task.Delay(TimeSpan.FromSeconds(5));

            return JobResult.Success;
        }
    }
}

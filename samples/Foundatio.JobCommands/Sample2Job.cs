using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Microsoft.Extensions.Logging;

namespace Exceptionless.Web {
    [Job(Description = "Sample 2 job", Interval = "2s", IterationLimit = 10)]
    public class Sample2Job : IJob {
        private readonly Microsoft.Extensions.Logging.ILogger _logger;
        private int _iterationCount = 0;

        public Sample2Job(ILoggerFactory loggerFactory) {
            _logger = loggerFactory.CreateLogger<Sample2Job>();
        }

        public Task<JobResult> RunAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            Interlocked.Increment(ref _iterationCount);
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Sample2Job Run #{IterationCount} Thread={ManagedThreadId}", _iterationCount, Thread.CurrentThread.ManagedThreadId);
            return Task.FromResult(JobResult.Success);
        }
    }
}
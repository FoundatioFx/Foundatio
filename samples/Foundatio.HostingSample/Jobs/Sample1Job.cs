using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Microsoft.Extensions.Logging;

namespace Foundatio.HostingSample;

[Job(Description = "Sample 1 job", Interval = "5s", IterationLimit = 5)]
public class Sample1Job : IJob
{
    private readonly ILogger _logger;
    private int _iterationCount = 0;

    public Sample1Job(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<Sample1Job>();
    }

    public Task<JobResult> RunAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _iterationCount);
        _logger.LogTrace("Sample1Job Run #{IterationCount} Thread={ManagedThreadId}", _iterationCount, Thread.CurrentThread.ManagedThreadId);

        return Task.FromResult(JobResult.Success);
    }
}

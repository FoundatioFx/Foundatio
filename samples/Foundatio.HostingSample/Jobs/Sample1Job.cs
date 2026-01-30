using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Resilience;
using Microsoft.Extensions.Logging;

namespace Foundatio.HostingSample;

[Job(Description = "Sample 1 job", Interval = "5s", IterationLimit = 5)]
public class Sample1Job : IJob
{
    private readonly IResiliencePolicy _policy;
    private readonly ILogger _logger;
    private int _iterationCount = 0;

    public Sample1Job(IResiliencePolicyProvider provider, ILoggerFactory loggerFactory)
    {
        // get policy for Sample1Job and if not found, try to get policy for IJob, then fallback to default policy
        _policy = provider.GetPolicy<Sample1Job, IJob>();
        _logger = loggerFactory.CreateLogger<Sample1Job>();
    }

    public async Task<JobResult> RunAsync(CancellationToken cancellationToken = default)
    {
        return await _policy.ExecuteAsync(async _ =>
        {
            int count = Interlocked.Increment(ref _iterationCount);
            _logger.LogTrace("Sample1Job Run #{IterationCount} Thread={ManagedThreadId}", _iterationCount, Thread.CurrentThread.ManagedThreadId);

            if (count < 3)
            {
                _logger.LogInformation("Sample1Job Run #{IterationCount} Thread={ManagedThreadId} - Simulating failure", _iterationCount, Thread.CurrentThread.ManagedThreadId);
                throw new InvalidOperationException("Simulated failure");
            }

            await Task.Delay(5000, cancellationToken);

            return JobResult.Success;
        }, cancellationToken);
    }
}

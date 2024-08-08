using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Foundatio.HostingSample;

[Job(Description = "Sample 2 job", Interval = "2s", IterationLimit = 10)]
public class Sample2Job : IJob, IHealthCheck
{
    private readonly ILogger _logger;
    private int _iterationCount = 0;
    private DateTime? _lastRun = null;

    public Sample2Job(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<Sample2Job>();
    }

    public Task<JobResult> RunAsync(CancellationToken cancellationToken = default)
    {
        _lastRun = DateTime.UtcNow;
        Interlocked.Increment(ref _iterationCount);
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogTrace("Sample2Job Run #{IterationCount} Thread={ManagedThreadId}", _iterationCount, Thread.CurrentThread.ManagedThreadId);

        return Task.FromResult(JobResult.Success);
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!_lastRun.HasValue)
            return Task.FromResult(HealthCheckResult.Healthy("Job has not been run yet."));

        if (DateTime.UtcNow.Subtract(_lastRun.Value) > TimeSpan.FromSeconds(5))
            return Task.FromResult(HealthCheckResult.Unhealthy("Job has not run in the last 5 seconds."));

        return Task.FromResult(HealthCheckResult.Healthy("Job has run in the last 5 seconds."));
    }
}

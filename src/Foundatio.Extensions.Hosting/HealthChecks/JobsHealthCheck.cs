using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Foundatio.Extensions.Hosting.HealthChecks;

/// <summary>Reports the job worker (with running jobs per job name) and the CRON scheduler of this process.</summary>
internal sealed class JobsHealthCheck : IHealthCheck
{
    private readonly IServiceProvider _services;
    private readonly TimeSpan _unhealthyAfter;

    public JobsHealthCheck(IServiceProvider services, TimeSpan unhealthyAfter)
    {
        _services = services;
        _unhealthyAfter = unhealthyAfter;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (_services.GetService<JobsStatus>() is not { } status)
            return Task.FromResult(new HealthCheckResult(context.Registration.FailureStatus, "Jobs are not configured: call AddFoundatioJobs."));

        var now = (_services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
        return Task.FromResult(ProcessingHealth.Evaluate(status.GetComponents(), "No jobs or CRON schedules are registered", now, _unhealthyAfter, context.Registration.FailureStatus));
    }
}

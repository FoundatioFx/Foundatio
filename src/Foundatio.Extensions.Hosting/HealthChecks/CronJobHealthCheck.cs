using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Foundatio.Extensions.Hosting.HealthChecks;

/// <summary>Reports whether a CRON schedule has succeeded recently, on any instance, from the job store.</summary>
internal sealed class CronJobHealthCheck : IHealthCheck
{
    private readonly IServiceProvider _services;
    private readonly string _scheduleName;
    private readonly TimeSpan _succeededWithin;

    public CronJobHealthCheck(IServiceProvider services, string scheduleName, TimeSpan succeededWithin)
    {
        _services = services;
        _scheduleName = scheduleName;
        _succeededWithin = succeededWithin;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (_services.GetService<JobsStatus>() is not { } status)
            return new HealthCheckResult(context.Registration.FailureStatus, "Jobs are not configured: call AddFoundatioJobs.");

        JobDetails? job;
        try
        {
            job = await status.GetLastSucceededAsync(_scheduleName, _succeededWithin, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, ex.Message);
        }

        if (job is null)
        {
            string window = TimeSpan.FromSeconds(Math.Floor(_succeededWithin.TotalSeconds)).ToString("c", CultureInfo.InvariantCulture);
            return new HealthCheckResult(context.Registration.FailureStatus, $"No occurrence of CRON schedule {_scheduleName} created in the last {window} has succeeded");
        }

        var data = new Dictionary<string, object> { ["jobId"] = job.Id, ["scheduledFor"] = job.ScheduledFor, ["createdAt"] = job.CreatedAt };
        if (job.CompletedAt is { } completedAt)
            data["completedAt"] = completedAt;

        return HealthCheckResult.Healthy($"CRON schedule {_scheduleName} last succeeded with the occurrence scheduled for {job.ScheduledFor.ToString("O", CultureInfo.InvariantCulture)}", data);
    }
}

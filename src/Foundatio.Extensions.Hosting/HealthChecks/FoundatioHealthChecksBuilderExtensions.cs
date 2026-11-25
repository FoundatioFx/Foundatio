using System;
using System.Collections.Generic;
using Foundatio.Extensions.Hosting.HealthChecks;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds health checks for Foundatio messaging and jobs.</summary>
public static class FoundatioHealthChecksBuilderExtensions
{
    private static readonly TimeSpan DefaultUnhealthyAfter = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Reports this process's message handlers, with one data entry per source they receive from. Healthy while starting
    /// (waiting for startup actions) and running; Degraded while receive or dead-letter errors are retried, with the last
    /// error in the description; <paramref name="failureStatus"/> when a processor stopped unexpectedly, startup failed,
    /// or errors have continued for <paramref name="unhealthyAfter"/>. The host fails to start if messaging is not configured.
    /// </summary>
    /// <remarks>
    /// Long-running errors are usually a broker outage, which restarting the process does not fix. Use the check for
    /// readiness, or pass <c>failureStatus: HealthStatus.Degraded</c> if a liveness probe uses it.
    /// </remarks>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="name">The health check name.</param>
    /// <param name="failureStatus">Reported instead of <see cref="HealthStatus.Unhealthy"/>. Defaults to Unhealthy.</param>
    /// <param name="tags">Tags to filter by, for example "ready".</param>
    /// <param name="unhealthyAfter">How long errors may be retried before the check fails. Defaults to 5 minutes.</param>
    /// <example>
    /// <code>
    /// builder.Services.AddHealthChecks()
    ///     .AddFoundatioMessaging(tags: ["ready"])
    ///     .AddFoundatioJobs(tags: ["ready"]);
    /// </code>
    /// </example>
    public static IHealthChecksBuilder AddFoundatioMessaging(this IHealthChecksBuilder builder, string name = "foundatio-messaging",
        HealthStatus? failureStatus = null, IEnumerable<string>? tags = null, TimeSpan? unhealthyAfter = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var threshold = ValidateThreshold(unhealthyAfter);
        AddRequirement(builder, new HealthCheckRequirement(name, typeof(MessagingStatus), "AddFoundatioMessaging"));
        return builder.Add(new HealthCheckRegistration(name, sp => new MessagingHealthCheck(sp, threshold), failureStatus, tags));
    }

    /// <summary>
    /// Reports this process's job worker, with running jobs per job name, and its CRON scheduler. Healthy while starting
    /// and running; Degraded while claim or scheduling errors are retried, with the last error in the description;
    /// <paramref name="failureStatus"/> when the worker stopped unexpectedly, startup failed, or errors have continued
    /// for <paramref name="unhealthyAfter"/>. A job that throws is the job's failure, not the worker's, and does not
    /// affect health. The host fails to start if jobs are not configured.
    /// </summary>
    /// <remarks>
    /// Long-running errors are usually a job store outage, which restarting the process does not fix. Use the check for
    /// readiness, or pass <c>failureStatus: HealthStatus.Degraded</c> if a liveness probe uses it.
    /// </remarks>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="name">The health check name.</param>
    /// <param name="failureStatus">Reported instead of <see cref="HealthStatus.Unhealthy"/>. Defaults to Unhealthy.</param>
    /// <param name="tags">Tags to filter by, for example "ready".</param>
    /// <param name="unhealthyAfter">How long errors may be retried before the check fails. Defaults to 5 minutes.</param>
    public static IHealthChecksBuilder AddFoundatioJobs(this IHealthChecksBuilder builder, string name = "foundatio-jobs",
        HealthStatus? failureStatus = null, IEnumerable<string>? tags = null, TimeSpan? unhealthyAfter = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var threshold = ValidateThreshold(unhealthyAfter);
        AddRequirement(builder, new HealthCheckRequirement(name, typeof(JobsStatus), "AddFoundatioJobs"));
        return builder.Add(new HealthCheckRegistration(name, sp => new JobsHealthCheck(sp, threshold), failureStatus, tags));
    }

    /// <summary>
    /// Reports whether the CRON schedule <paramref name="scheduleName"/> has succeeded recently: Healthy when one of its
    /// occurrences created in the last <paramref name="succeededWithin"/> has succeeded, on whichever instance ran it,
    /// or when the schedule is new, and <paramref name="failureStatus"/> when it is overdue (see
    /// <see cref="JobsStatus.IsScheduleOverdueAsync"/>). It reads the job store, so every instance reports the same, and
    /// costs one store query per check while the schedule succeeds. The host fails to start if jobs are not configured;
    /// a schedule this process doesn't register reports <paramref name="failureStatus"/>.
    /// </summary>
    /// <remarks>
    /// Make <paramref name="succeededWithin"/> longer than the time between occurrences plus the job's longest run, for
    /// example 15 seconds for a schedule every 5 seconds. A new schedule, such as an hourly one on a fresh job store, is
    /// Healthy until it has been known for <paramref name="succeededWithin"/> or one of its jobs fails.
    /// </remarks>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="scheduleName">The schedule: <see cref="CronJobOptions.Name"/>, which defaults to the job name.</param>
    /// <param name="succeededWithin">How recent an occurrence that succeeded must be.</param>
    /// <param name="name">The health check name. Defaults to <c>foundatio-cron-</c> followed by the schedule name.</param>
    /// <param name="failureStatus">Reported instead of <see cref="HealthStatus.Unhealthy"/>. Defaults to Unhealthy.</param>
    /// <param name="tags">Tags to filter by.</param>
    /// <example>
    /// <code>
    /// builder.Services.AddHealthChecks()
    ///     .AddFoundatioCronJob("stack-event-count", TimeSpan.FromSeconds(15), tags: ["jobs"]);
    /// </code>
    /// </example>
    public static IHealthChecksBuilder AddFoundatioCronJob(this IHealthChecksBuilder builder, string scheduleName, TimeSpan succeededWithin, string? name = null,
        HealthStatus? failureStatus = null, IEnumerable<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(scheduleName);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(succeededWithin, TimeSpan.Zero);
        if (name is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(name);

        name ??= $"foundatio-cron-{scheduleName}";
        AddRequirement(builder, new HealthCheckRequirement(name, typeof(JobsStatus), "AddFoundatioJobs"));
        return builder.Add(new HealthCheckRegistration(name, sp => new CronJobHealthCheck(sp, scheduleName, succeededWithin), failureStatus, tags));
    }

    private static void AddRequirement(IHealthChecksBuilder builder, HealthCheckRequirement requirement)
    {
        builder.Services.AddSingleton(requirement);
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, HealthCheckRequirementsValidator>());
    }

    private static TimeSpan ValidateThreshold(TimeSpan? unhealthyAfter)
    {
        var threshold = unhealthyAfter ?? DefaultUnhealthyAfter;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(threshold, TimeSpan.Zero, nameof(unhealthyAfter));
        return threshold;
    }
}

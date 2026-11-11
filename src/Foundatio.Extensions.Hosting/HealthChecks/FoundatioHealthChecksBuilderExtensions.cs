using System;
using System.Collections.Generic;
using Foundatio.Extensions.Hosting.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Microsoft.Extensions.DependencyInjection;

public static class FoundatioHealthChecksBuilderExtensions
{
    private static readonly TimeSpan DefaultUnhealthyAfter = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Reports this process's message handlers, with one data entry per source they receive from. Healthy while starting
    /// (waiting for startup actions) and running; Degraded while receive errors are retried, with the last error in the
    /// description; <paramref name="failureStatus"/> when a processor stopped unexpectedly, startup failed, or receiving
    /// has failed continuously for <paramref name="unhealthyAfter"/>.
    /// </summary>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="name">The health check name.</param>
    /// <param name="failureStatus">Reported instead of <see cref="HealthStatus.Unhealthy"/>. Defaults to Unhealthy.</param>
    /// <param name="tags">Tags to filter by, for example "live".</param>
    /// <param name="unhealthyAfter">How long errors may be retried before the check fails. Defaults to 5 minutes.</param>
    /// <example>
    /// <code>
    /// builder.Services.AddHealthChecks()
    ///     .AddFoundatioMessaging(tags: ["live"])
    ///     .AddFoundatioJobs(tags: ["live"]);
    /// </code>
    /// </example>
    public static IHealthChecksBuilder AddFoundatioMessaging(this IHealthChecksBuilder builder, string name = "foundatio-messaging",
        HealthStatus? failureStatus = null, IEnumerable<string>? tags = null, TimeSpan? unhealthyAfter = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var threshold = ValidateThreshold(unhealthyAfter);
        return builder.Add(new HealthCheckRegistration(name, sp => new MessagingHealthCheck(sp, threshold), failureStatus, tags));
    }

    /// <summary>
    /// Reports this process's job worker, with running jobs per job name, and its CRON scheduler. Healthy while starting
    /// and running; Degraded while claim or scheduling errors are retried, with the last error in the description;
    /// <paramref name="failureStatus"/> when the worker stopped unexpectedly, startup failed, or errors have continued
    /// for <paramref name="unhealthyAfter"/>. A job that throws is the job's failure, not the worker's, and does not
    /// affect health.
    /// </summary>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="name">The health check name.</param>
    /// <param name="failureStatus">Reported instead of <see cref="HealthStatus.Unhealthy"/>. Defaults to Unhealthy.</param>
    /// <param name="tags">Tags to filter by, for example "live".</param>
    /// <param name="unhealthyAfter">How long errors may be retried before the check fails. Defaults to 5 minutes.</param>
    public static IHealthChecksBuilder AddFoundatioJobs(this IHealthChecksBuilder builder, string name = "foundatio-jobs",
        HealthStatus? failureStatus = null, IEnumerable<string>? tags = null, TimeSpan? unhealthyAfter = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var threshold = ValidateThreshold(unhealthyAfter);
        return builder.Add(new HealthCheckRegistration(name, sp => new JobsHealthCheck(sp, threshold), failureStatus, tags));
    }

    private static TimeSpan ValidateThreshold(TimeSpan? unhealthyAfter)
    {
        var threshold = unhealthyAfter ?? DefaultUnhealthyAfter;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(threshold, TimeSpan.Zero, nameof(unhealthyAfter));
        return threshold;
    }
}

using System;

namespace Foundatio.Jobs;

/// <summary>Settings for a CRON schedule.</summary>
public sealed record CronJobOptions
{
    /// <summary>Identifies the schedule. Defaults to the job name. Must be unique.</summary>
    public string? Name { get; init; }

    /// <summary>The time zone the expression is evaluated in. Defaults to UTC.</summary>
    public TimeZoneInfo? TimeZone { get; init; }

    /// <summary>
    /// An occurrence missed because no instance was running is still run if an instance starts within this time.
    /// Only the most recent missed occurrence runs. Defaults to one hour.
    /// </summary>
    public TimeSpan MisfireThreshold { get; init; } = TimeSpan.FromHours(1);

    /// <summary>When false (the default), an occurrence is skipped while the previous one is still queued or running.</summary>
    public bool AllowConcurrent { get; init; }

    /// <summary>
    /// How long the schedule's jobs are kept after they succeed. Zero keeps none for longer than running each
    /// occurrence once needs: until the next occurrence is due, or <see cref="MisfireThreshold"/> has passed if that is
    /// sooner, plus a minute. Use it for schedules that run every few seconds, which would otherwise keep a record of
    /// every run for <see cref="JobWorkerOptions.RetainSucceededFor"/>. Defaults to the job type's
    /// <see cref="JobTypeOptions.RetainSucceededFor"/>, then <see cref="JobWorkerOptions.RetainSucceededFor"/>. Applied by
    /// the processes that register the schedule and run its job, as <c>AddCronJob&lt;TJob&gt;</c> does.
    /// </summary>
    public TimeSpan? RetainSucceededFor { get; init; }

    /// <summary>
    /// Settings for the job type, applied by <c>AddCronJob&lt;TJob&gt;(cronExpression, options)</c>, which registers the
    /// job, as if passed to <c>AddJob&lt;TJob&gt;(options)</c>. A schedule of job arguments does not register the job, so it
    /// throws when this is set.
    /// </summary>
    public JobTypeOptions? JobTypeOptions { get; init; }
}

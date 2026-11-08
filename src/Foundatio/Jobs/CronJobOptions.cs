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

    /// <summary>Settings for the job type, applied when this registers it.</summary>
    public JobTypeOptions? JobOptions { get; init; }
}

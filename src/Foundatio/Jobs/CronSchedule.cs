using System;

namespace Foundatio.Jobs;

internal sealed record CronSchedule(string Name, string JobName, Cronos.CronExpression Expression, object? Arguments, CronJobOptions Options)
{
    public TimeZoneInfo TimeZone => Options.TimeZone ?? TimeZoneInfo.Utc;
}

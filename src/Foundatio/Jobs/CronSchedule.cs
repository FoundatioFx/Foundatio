using System;
using System.Globalization;

namespace Foundatio.Jobs;

internal sealed record CronSchedule(string Name, string JobName, Cronos.CronExpression Expression, object? Arguments, CronJobOptions Options)
{
    private static readonly TimeSpan MaxLookBack = TimeSpan.FromDays(366 * 30);

    public TimeZoneInfo TimeZone => Options.TimeZone ?? TimeZoneInfo.Utc;

    /// <summary>The id of an occurrence's job. Every instance derives the same id, so the store keeps one job per occurrence.</summary>
    public string GetJobId(DateTimeOffset occurrence) => $"cron:{Name}:{occurrence.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}";

    /// <summary>The latest occurrence before <paramref name="before"/>, or null when there is none in the previous 30 years.</summary>
    public DateTimeOffset? GetPreviousOccurrence(DateTimeOffset before)
    {
        // The expression only searches forward: widen the window back from `before` until it holds an occurrence,
        // then narrow it to the latest start whose next occurrence is still before `before`. Occurrences are at
        // least a second apart, so a start within a second of that boundary finds the latest one.
        var window = TimeSpan.FromSeconds(1);
        while (!(Expression.GetNextOccurrence(before - window, TimeZone, inclusive: true) < before))
        {
            if (window >= MaxLookBack)
                return null;

            window += window;
        }

        long low = (before - window).UtcTicks;
        long high = before.UtcTicks;
        while (high - low > TimeSpan.TicksPerSecond)
        {
            long middle = low + (high - low) / 2;
            if (Expression.GetNextOccurrence(new DateTimeOffset(middle, TimeSpan.Zero), TimeZone, inclusive: true) < before)
                low = middle;
            else
                high = middle;
        }

        return Expression.GetNextOccurrence(new DateTimeOffset(low, TimeSpan.Zero), TimeZone, inclusive: true);
    }
}

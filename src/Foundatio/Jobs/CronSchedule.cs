using System;
using System.Globalization;

namespace Foundatio.Jobs;

internal sealed record CronSchedule(string Name, string JobName, Cronos.CronExpression Expression, object? Arguments, CronJobOptions Options)
{
    private static readonly TimeSpan MaxLookBack = TimeSpan.FromDays(366 * 30);

    /// <summary>Added to how long an occurrence's job is kept, for schedulers whose clocks are behind the store's.</summary>
    private static readonly TimeSpan ClockMargin = TimeSpan.FromMinutes(1);

    public TimeZoneInfo TimeZone => Options.TimeZone ?? TimeZoneInfo.Utc;

    /// <summary>The id of an occurrence's job. Every instance derives the same id, so the store keeps one job per occurrence.</summary>
    public string GetJobId(DateTimeOffset occurrence) => $"cron:{Name}:{occurrence.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Until when the job of <paramref name="occurrence"/> must be kept so no scheduler creates it again: a scheduler
    /// creates an occurrence while it is the latest one and within <see cref="CronJobOptions.MisfireThreshold"/>, so
    /// until the next occurrence is due or the threshold has passed, whichever is first, plus a margin for clocks.
    /// </summary>
    public DateTimeOffset GetKeepUntil(DateTimeOffset occurrence)
    {
        var until = occurrence + Options.MisfireThreshold;
        if (Expression.GetNextOccurrence(occurrence, TimeZone, inclusive: false) is { } next && next < until)
            until = next;

        return until + ClockMargin;
    }

    /// <summary>
    /// The latest occurrence from <paramref name="from"/> to <paramref name="to"/>, both inclusive, or null when there is
    /// none. A binary search, so a schedule that fires every second costs no more to check than an hourly one.
    /// </summary>
    public DateTimeOffset? GetLatestOccurrence(DateTimeOffset from, DateTimeOffset to)
    {
        if (!(Expression.GetNextOccurrence(from, TimeZone, inclusive: true) <= to))
            return null;

        // The next occurrence from `low` is never after `to`, and none is from `high` to `to`. Occurrences are at least
        // a second apart, so once `high` is within a second of `low`, the next occurrence from `low` is the latest.
        long low = from.UtcTicks;
        long high = to.UtcTicks + 1;
        while (high - low > TimeSpan.TicksPerSecond)
        {
            long middle = low + (high - low) / 2;
            if (Expression.GetNextOccurrence(new DateTimeOffset(middle, TimeSpan.Zero), TimeZone, inclusive: true) <= to)
                low = middle;
            else
                high = middle;
        }

        return Expression.GetNextOccurrence(new DateTimeOffset(low, TimeSpan.Zero), TimeZone, inclusive: true);
    }

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

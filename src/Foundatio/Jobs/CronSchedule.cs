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
        if (GetNextOccurrence(occurrence, inclusive: false) is { } next && next < until)
            until = next;

        return until + ClockMargin;
    }

    /// <summary>
    /// The latest occurrence from <paramref name="from"/> to <paramref name="to"/>, both inclusive, or null when there is
    /// none. A binary search, so a schedule that fires every second costs no more to check than an hourly one.
    /// </summary>
    public DateTimeOffset? GetLatestOccurrence(DateTimeOffset from, DateTimeOffset to)
    {
        if (!(GetNextOccurrence(from, inclusive: true) <= to))
            return null;

        // The next occurrence from `low` is never after `to`, and none is from `high` to `to`. Occurrences are at least
        // a second apart, so once `high` is within a second of `low`, the next occurrence from `low` is the latest.
        long low = from.UtcTicks;
        long high = to.UtcTicks + 1;
        while (high - low > TimeSpan.TicksPerSecond)
        {
            long middle = low + (high - low) / 2;
            if (GetNextOccurrence(new DateTimeOffset(middle, TimeSpan.Zero), inclusive: true) <= to)
                low = middle;
            else
                high = middle;
        }

        return GetNextOccurrence(new DateTimeOffset(low, TimeSpan.Zero), inclusive: true);
    }

    /// <summary>
    /// The first occurrence after <paramref name="from"/>, or at it when <paramref name="inclusive"/>; null when there is
    /// none. Cronos returns an earlier time when the local time it finds was skipped by a change of the zone's base
    /// offset, which .NET doesn't report as an invalid time (Samoa skipped 2011-12-30); the search then starts again
    /// where the offset changed, so occurrences never go back in time.
    /// </summary>
    public DateTimeOffset? GetNextOccurrence(DateTimeOffset from, bool inclusive = false)
    {
        var next = Expression.GetNextOccurrence(from, TimeZone, inclusive);
        if (next is not { } found || found > from || inclusive && found == from)
            return next;

        // The local time found, read with the offset in use at `from`, is after the change: find the change to the second.
        var offset = TimeZone.GetUtcOffset(from);
        long low = from.UtcTicks;
        long high = new DateTimeOffset(found.DateTime, offset).UtcTicks;
        if (high <= low || TimeZone.GetUtcOffset(new DateTimeOffset(high, TimeSpan.Zero)) == offset)
            return null;

        while (high - low > TimeSpan.TicksPerSecond)
        {
            long middle = low + (high - low) / 2;
            if (TimeZone.GetUtcOffset(new DateTimeOffset(middle, TimeSpan.Zero)) == offset)
                low = middle;
            else
                high = middle;
        }

        next = Expression.GetNextOccurrence(new DateTimeOffset(high, TimeSpan.Zero), TimeZone, inclusive: true);
        return next > from ? next : null;
    }

    /// <summary>The latest occurrence before <paramref name="before"/>, or null when there is none in the previous 30 years.</summary>
    public DateTimeOffset? GetPreviousOccurrence(DateTimeOffset before)
    {
        // The expression only searches forward: widen the window back from `before` until it holds an occurrence,
        // then narrow it to the latest start whose next occurrence is still before `before`. Occurrences are at
        // least a second apart, so a start within a second of that boundary finds the latest one.
        var window = TimeSpan.FromSeconds(1);
        while (!(GetNextOccurrence(before - window, inclusive: true) < before))
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
            if (GetNextOccurrence(new DateTimeOffset(middle, TimeSpan.Zero), inclusive: true) < before)
                low = middle;
            else
                high = middle;
        }

        return GetNextOccurrence(new DateTimeOffset(low, TimeSpan.Zero), inclusive: true);
    }
}

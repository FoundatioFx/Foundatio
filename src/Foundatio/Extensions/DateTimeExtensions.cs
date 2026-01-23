using System;

namespace Foundatio.Utility;

internal static class DateTimeExtensions
{
    public static DateTime GetUtcNowDateTime(this TimeProvider timeProvider, bool includeMilliseconds = true)
    {
        var utcNow = timeProvider.GetUtcNow();
        return includeMilliseconds ? utcNow.UtcDateTime : utcNow.UtcDateTime.AddTicks(-(utcNow.UtcDateTime.Ticks % TimeSpan.TicksPerSecond));
    }

    public static DateTime Floor(this DateTime date, TimeSpan interval)
    {
        return date.AddTicks(-(date.Ticks % interval.Ticks));
    }

    public static DateTime Ceiling(this DateTime date, TimeSpan interval)
    {
        return date.AddTicks(interval.Ticks - (date.Ticks % interval.Ticks));
    }

    public static long ToUnixTimeMilliseconds(this DateTime date)
    {
        return new DateTimeOffset(date.ToUniversalTime()).ToUnixTimeMilliseconds();
    }

    public static DateTime FromUnixTimeMilliseconds(this long timestamp)
    {
        return DateTimeOffset.FromUnixTimeMilliseconds(timestamp).UtcDateTime;
    }

    public static long ToUnixTimeSeconds(this DateTime date)
    {
        return new DateTimeOffset(date.ToUniversalTime()).ToUnixTimeSeconds();
    }

    public static DateTimeOffset FromUnixTimeSeconds(this long timestamp)
    {
        return DateTimeOffset.FromUnixTimeSeconds(timestamp);
    }

    public static DateTime SafeAdd(this DateTime date, TimeSpan value)
    {
        // Check for overflow before adding to avoid integer wraparound
        if (value.Ticks > 0 && date.Ticks > DateTime.MaxValue.Ticks - value.Ticks)
            return DateTime.MaxValue;

        if (value.Ticks < 0 && date.Ticks < DateTime.MinValue.Ticks - value.Ticks)
            return DateTime.MinValue;

        return date.Add(value);
    }

    public static DateTimeOffset SafeAdd(this DateTimeOffset date, TimeSpan value)
    {
        // Check for overflow before adding to avoid integer wraparound
        if (value.Ticks > 0 && date.Ticks > DateTimeOffset.MaxValue.Ticks - value.Ticks)
            return DateTimeOffset.MaxValue;

        if (value.Ticks < 0 && date.Ticks < DateTimeOffset.MinValue.Ticks - value.Ticks)
            return DateTimeOffset.MinValue;

        return date.Add(value);
    }

    public static DateTimeOffset SafeAddMilliseconds(this DateTimeOffset date, double milliseconds)
    {
        // Check for overflow before creating TimeSpan to avoid exception in TimeSpan.FromMilliseconds
        if (milliseconds > TimeSpan.MaxValue.TotalMilliseconds)
            return DateTimeOffset.MaxValue;

        if (milliseconds < TimeSpan.MinValue.TotalMilliseconds)
            return DateTimeOffset.MinValue;

        return date.SafeAdd(TimeSpan.FromMilliseconds(milliseconds));
    }

    public static DateTime SafeAddMilliseconds(this DateTime date, double milliseconds)
    {
        // Check for overflow before creating TimeSpan to avoid exception in TimeSpan.FromMilliseconds
        if (milliseconds > TimeSpan.MaxValue.TotalMilliseconds)
            return DateTime.MaxValue;

        if (milliseconds < TimeSpan.MinValue.TotalMilliseconds)
            return DateTime.MinValue;

        return date.SafeAdd(TimeSpan.FromMilliseconds(milliseconds));
    }
}

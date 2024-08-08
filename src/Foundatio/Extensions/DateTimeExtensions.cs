using System;

namespace Foundatio.Utility;

internal static class DateTimeExtensions
{
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
        if (date.Ticks + value.Ticks < DateTime.MinValue.Ticks)
            return DateTime.MinValue;

        if (date.Ticks + value.Ticks > DateTime.MaxValue.Ticks)
            return DateTime.MaxValue;

        return date.Add(value);
    }
}

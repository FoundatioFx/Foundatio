using System;

namespace Foundatio.Redis.Extensions;

internal static class DateTimeExtensions
{
    public static long ToUnixTimeMilliseconds(this DateTime date)
    {
        return new DateTimeOffset(date.ToUniversalTime()).ToUnixTimeMilliseconds();
    }

    public static DateTime FromUnixTimeMilliseconds(this long timestamp)
    {
        return DateTimeOffset.FromUnixTimeMilliseconds(timestamp).UtcDateTime;
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
}

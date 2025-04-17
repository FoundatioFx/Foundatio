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
        if (date.Ticks + value.Ticks < DateTime.MinValue.Ticks)
            return DateTime.MinValue;

        if (date.Ticks + value.Ticks > DateTime.MaxValue.Ticks)
            return DateTime.MaxValue;

        return date.Add(value);
    }
}

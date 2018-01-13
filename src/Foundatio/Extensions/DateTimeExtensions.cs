using System;

namespace Foundatio.Utility {
    internal static class DateTimeExtensions {
        public static DateTime Floor(this DateTime date, TimeSpan interval) {
            return date.AddTicks(-(date.Ticks % interval.Ticks));
        }

        public static DateTime Ceiling(this DateTime date, TimeSpan interval) {
            return date.AddTicks(interval.Ticks - (date.Ticks % interval.Ticks));
        }

        public static long ToUnixTimeSeconds(this DateTime date) {
            return new DateTimeOffset(date).ToUnixTimeSeconds();
        }
        
        public static DateTime FromUnixTimeSeconds(this long timestamp) {
            return DateTimeOffset.FromUnixTimeSeconds(timestamp).DateTime;
        }
    }
}

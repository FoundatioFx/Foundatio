using System;

namespace Foundatio.Extensions {
    internal static class DateTimeExtensions {
        public static DateTime Floor(this DateTime date, TimeSpan interval) {
            return date.AddTicks(-(date.Ticks % interval.Ticks));
        }

        public static DateTime Ceiling(this DateTime date, TimeSpan interval) {
            return date.AddTicks(interval.Ticks - (date.Ticks % interval.Ticks));
        }
    }
}

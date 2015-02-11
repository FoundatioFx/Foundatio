using System;

namespace Foundatio.Extensions {
    public static class DateTimeExtensions {
        public static DateTime Floor(this DateTime date, TimeSpan interval) {
            return date.AddTicks(-(date.Ticks % interval.Ticks));
        }
    }
}

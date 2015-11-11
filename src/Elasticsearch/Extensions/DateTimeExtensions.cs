using System;

namespace Foundatio.Elasticsearch.Extensions {
    public static class DateTimeExtensions {
        public static DateTime EndOfDay(this DateTime date) {
            if (date == DateTime.MaxValue)
                return date;

            return date.Date.AddDays(1).SubtractMilliseconds(1);
        }

        public static DateTime SubtractMilliseconds(this DateTime date, double value) {
            if (value < 0)
                throw new ArgumentException("Value cannot be less than 0.", nameof(value));

            return date.AddMilliseconds(value * -1);
        }
    }
}
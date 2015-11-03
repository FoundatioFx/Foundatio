using System;
using System.Collections.Generic;

namespace Foundatio.Elasticsearch.Repositories {
    public interface IDateRangeQuery {
        List<DateRange> DateRanges { get; }
    }

    public class DateRange {
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public string Field { get; set; }

        public bool UseStartDate => StartDate.HasValue && StartDate.Value > DateTime.MinValue;

        public bool UseEndDate => EndDate.HasValue && EndDate.Value < DateTime.UtcNow.AddHours(1);

        public bool UseDateRange => Field != null && (UseStartDate || UseEndDate);

        public DateTime GetStartDate() {
            return UseStartDate ? StartDate.GetValueOrDefault() : DateTime.MinValue;
        }

        public DateTime GetEndDate() {
            return UseEndDate ? EndDate.GetValueOrDefault() : DateTime.UtcNow.AddHours(1);
        }
    }

    public static class DateRangeQueryExtensions {
        public static T WithDateRange<T>(this T query, DateTime? start, DateTime? end, string field) where T : IDateRangeQuery {
            query.DateRanges.Add(new DateRange { StartDate = start, EndDate = end, Field = field });
            return query;
        }
    }
}

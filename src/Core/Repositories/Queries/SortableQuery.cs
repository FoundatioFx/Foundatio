using System;
using System.Collections.Generic;

namespace Foundatio.Repositories {
    public interface ISortableQuery {
        List<FieldSort> SortBy { get; }
        bool SortByScore { get; set; }
    }

    public static class SortableQueryExtensions {
        public static T WithSort<T>(this T query, string field, SortOrder? sortOrder = null) where T : ISortableQuery {
            if (!String.IsNullOrEmpty(field))
                query.SortBy.Add(new FieldSort { Field = field, Order = sortOrder });
            return query;
        }

        public static T WithSort<T>(this T query, SortingOptions sorting) where T : ISortableQuery {
            if (sorting != null)
                query.SortBy.AddRange(sorting.Fields);
            return query;
        }

        public static T WithScoring<T>(this T query) where T : ISortableQuery {
            query.SortByScore = true;
            return query;
        }
    }
}

using System;

namespace Foundatio.Elasticsearch.Repositories.Queries {
    public interface ISearchQuery {
        string SystemFilter { get; set; }
        string Filter { get; set; }
        string SearchQuery { get; set; }
        SearchOperator DefaultSearchQueryOperator { get; set; }
    }

    public enum SearchOperator {
        And,
        Or
    }

    public static class SearchQueryExtensions {
        public static T WithSystemFilter<T>(this T query, string filter) where T : ISearchQuery {
            query.SystemFilter = filter;
            return query;
        }

        public static T WithFilter<T>(this T query, string filter) where T : ISearchQuery {
            query.Filter = filter;
            return query;
        }

        public static T WithSearchQuery<T>(this T query, string queryString, bool useAndAsDefaultOperator = true) where T : ISearchQuery {
            query.SearchQuery = queryString;
            query.DefaultSearchQueryOperator = useAndAsDefaultOperator ? SearchOperator.And : SearchOperator.Or;
            return query;
        }
    }
}

using System;
using Foundatio.Repositories;
using Nest;

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

    public class SearchQueryBuilder : QueryBuilderBase {
        public override void BuildQuery<T>(IReadOnlyRepository<T> repository, QueryContainer container, object query) {
            var searchQuery = query as ISearchQuery;
            if (String.IsNullOrEmpty(searchQuery?.SearchQuery))
                return;

            container &= new QueryStringQuery {
                Query = searchQuery.SearchQuery,
                DefaultOperator = searchQuery.DefaultSearchQueryOperator == SearchOperator.Or ? Operator.Or : Operator.And,
                AnalyzeWildcard = true
            };
        }

        public override void BuildFilter<T>(IReadOnlyRepository<T> repository, FilterContainer container, object query) {
            var searchQuery = query as ISearchQuery;
            if (searchQuery == null)
                return;

            if (!String.IsNullOrEmpty(searchQuery.SystemFilter)) {
                container &= new QueryFilter {
                    Query = QueryContainer.From(new QueryStringQuery {
                        Query = searchQuery.SystemFilter,
                        DefaultOperator = Operator.And
                    })
                };
            }

            if (!String.IsNullOrEmpty(searchQuery.Filter)) {
                container &= new QueryFilter {
                    Query = QueryContainer.From(new QueryStringQuery {
                        Query = searchQuery.Filter,
                        DefaultOperator = Operator.And
                    })
                };
            }
        }
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

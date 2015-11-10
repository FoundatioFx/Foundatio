using System;
using Nest;

namespace Foundatio.Elasticsearch.Repositories.Queries.Builders {
    public class SearchQueryBuilder : QueryBuilderBase {
        public override void BuildQuery<T>(object query, object options, QueryContainer container) {
            var searchQuery = query as ISearchQuery;
            if (String.IsNullOrEmpty(searchQuery?.SearchQuery))
                return;

            container &= new QueryStringQuery {
                Query = searchQuery.SearchQuery,
                DefaultOperator = searchQuery.DefaultSearchQueryOperator == SearchOperator.Or ? Operator.Or : Operator.And,
                AnalyzeWildcard = true
            };
        }

        public override void BuildFilter<T>(object query, object options, FilterContainer container) {
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
}
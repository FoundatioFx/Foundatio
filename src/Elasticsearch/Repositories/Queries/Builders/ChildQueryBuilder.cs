using System;
using Nest;

namespace Foundatio.Elasticsearch.Repositories.Queries.Builders {
    public class ChildQueryBuilder : QueryBuilderBase {
        private readonly QueryBuilderRegistry _queryBuilder;

        public ChildQueryBuilder(QueryBuilderRegistry queryBuilder) {
            _queryBuilder = queryBuilder;
        }

        public override void BuildFilter<T>(object query, object options, FilterContainer container) {
            var childQuery = query as IChildQuery;
            if (childQuery?.ChildQuery == null)
                return;
            
            container &= new HasChildFilter {
                Query = _queryBuilder.BuildQuery<T>(childQuery.ChildQuery, options),
                Type = childQuery.ChildQuery.Type
            };
        }
    }
}
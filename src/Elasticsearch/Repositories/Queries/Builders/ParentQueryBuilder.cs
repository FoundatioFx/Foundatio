using System;
using Nest;

namespace Foundatio.Elasticsearch.Repositories.Queries.Builders {
    public class ParentQueryBuilder : QueryBuilderBase {
        private readonly QueryBuilderRegistry _queryBuilder;

        public ParentQueryBuilder(QueryBuilderRegistry queryBuilder) {
            _queryBuilder = queryBuilder;
        }

        public override void BuildFilter<T>(object query, object options, ref FilterContainer container) {
            var parentQuery = query as IParentQuery;
            if (parentQuery?.ParentQuery == null)
                return;
            
            container &= new HasParentFilter {
                Query = _queryBuilder.BuildQuery<T>(parentQuery.ParentQuery, options),
                Type = parentQuery.ParentQuery.Type
            };
        }
    }
}
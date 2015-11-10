using System;
using Foundatio.Repositories.Queries;
using Nest;

namespace Foundatio.Elasticsearch.Repositories.Queries.Builders {
    public class IdentityQueryBuilder : QueryBuilderBase {
        public override void BuildFilter<T>(object query, object options, FilterContainer container) {
            var identityQuery = query as IIdentityQuery;
            if (identityQuery?.Ids == null || identityQuery.Ids.Count <= 0)
                return;

            container &= new IdsFilter { Values = identityQuery.Ids };
        }
    }
}
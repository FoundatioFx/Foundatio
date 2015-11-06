using System;
using Foundatio.Elasticsearch.Repositories.Queries;
using Foundatio.Repositories;
using Foundatio.Repositories.Queries;
using Nest;

namespace Foundatio.Elasticsearch.Repositories.Query.Parts {
    public class IdentityQueryBuilder : QueryBuilderBase {
        public override void BuildFilter<T>(IReadOnlyRepository<T> repository, FilterContainer container, object query) {
            var identityQuery = query as IIdentityQuery;
            if (identityQuery == null || identityQuery.Ids.Count <= 0)
                return;

            container &= new IdsFilter { Values = identityQuery.Ids };
        }
    }
}
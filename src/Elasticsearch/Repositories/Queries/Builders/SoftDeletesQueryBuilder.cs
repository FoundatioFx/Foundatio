using System;
using Foundatio.Elasticsearch.Repositories.Queries.Options;
using Foundatio.Repositories.Queries;
using Nest;

namespace Foundatio.Elasticsearch.Repositories.Queries.Builders {
    public class SoftDeletesQueryBuilder : QueryBuilderBase {
        public override void BuildFilter<T>(object query, object options, ref FilterContainer container) {
            var softDeletesQuery = query as ISoftDeletesQuery;
            if (softDeletesQuery == null)
                return;

            var opt = options as IQueryOptions;
            if (opt == null || !opt.SupportsSoftDeletes)
                return;
            
            container &= new TermFilter { Field = "deleted", Value = softDeletesQuery.IncludeSoftDeletes };
        }
    }
}
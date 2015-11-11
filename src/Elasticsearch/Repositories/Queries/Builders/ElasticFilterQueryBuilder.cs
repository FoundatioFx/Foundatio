using System;
using Nest;

namespace Foundatio.Elasticsearch.Repositories.Queries.Builders {
    public class ElasticFilterQueryBuilder : QueryBuilderBase {
        public override void BuildFilter<T>(object query, object options, ref FilterContainer container) {
            var elasticQuery = query as IElasticFilterQuery;
            if (elasticQuery?.ElasticFilter == null)
                return;

            container &= elasticQuery.ElasticFilter;
        }
    }
}
using System;
using Nest;

namespace Foundatio.Elasticsearch.Repositories.Queries.Builders {
    public class ElasticFilterQueryBuilder : QueryBuilderBase {
        public override void BuildFilter(object query, object options, FilterContainer container) {
            var elasticQuery = query as IElasticFilterQuery;
            if (elasticQuery?.ElasticFilter == null)
                return;

            container &= elasticQuery.ElasticFilter;
        }
    }
}
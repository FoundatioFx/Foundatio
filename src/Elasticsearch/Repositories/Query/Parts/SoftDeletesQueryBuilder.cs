using System;
using Foundatio.Elasticsearch.Repositories.Queries;
using Foundatio.Repositories;
using Foundatio.Repositories.Queries;
using Nest;

namespace Foundatio.Elasticsearch.Repositories.Query.Parts {
    public class SoftDeletesQueryBuilder : QueryBuilderBase {
        public override void BuildFilter<T>(IReadOnlyRepository<T> repository, FilterContainer container, object query) {
            var softDeletesQuery = query as ISoftDeletesQuery;
            if (softDeletesQuery == null)
                return;

            var elasticRepo = repository as ElasticReadOnlyRepositoryBase<T>;
            if (elasticRepo == null || !elasticRepo.SupportsSoftDeletes)
                return;
            
            container &= new TermFilter { Field = "deleted", Value = softDeletesQuery.IncludeSoftDeletes };
        }
    }
}
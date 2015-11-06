using System;
using System.Linq;
using Foundatio.Elasticsearch.Repositories.Queries;
using Foundatio.Repositories;
using Foundatio.Repositories.Queries;
using Nest;

namespace Foundatio.Elasticsearch.Repositories.Query.Parts {
    public class SortableQueryBuilder : QueryBuilderBase {
        public override void BuildSearch<T>(IReadOnlyRepository<T> repository, SearchDescriptor<T> descriptor, object query) {
            var sortableQuery = query as ISortableQuery;
            if (sortableQuery == null || sortableQuery.SortBy.Count <= 0)
                return;

            var elasticRepo = repository as ElasticReadOnlyRepositoryBase<T>;
            foreach (var sort in sortableQuery.SortBy.Where(s => CanSortByField(elasticRepo?.AllowedSortFields, s.Field)))
                descriptor.Sort(s => s.OnField(sort.Field)
                    .Order(sort.Order == Foundatio.Repositories.Models.SortOrder.Ascending ? SortOrder.Ascending : SortOrder.Descending));
        }

        protected bool CanSortByField(string[] allowedFields, string field) {
            // allow all fields if an allowed list isn't specified
            if (allowedFields == null || allowedFields.Length == 0)
                return true;

            return allowedFields.Contains(field, StringComparer.OrdinalIgnoreCase);
        }
    }
}
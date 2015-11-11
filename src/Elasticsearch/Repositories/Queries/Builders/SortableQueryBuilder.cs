using System;
using System.Linq;
using Foundatio.Elasticsearch.Repositories.Queries.Options;
using Foundatio.Repositories.Queries;
using Nest;

namespace Foundatio.Elasticsearch.Repositories.Queries.Builders {
    public class SortableQueryBuilder : QueryBuilderBase {
        public override void BuildSearch<T>(object query, object options, ref SearchDescriptor<T> descriptor) {
            var sortableQuery = query as ISortableQuery;
            if (sortableQuery?.SortBy == null || sortableQuery.SortBy.Count <= 0)
                return;

            var opt = options as IQueryOptions;
            foreach (var sort in sortableQuery.SortBy.Where(s => CanSortByField(opt?.AllowedSortFields, s.Field)))
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
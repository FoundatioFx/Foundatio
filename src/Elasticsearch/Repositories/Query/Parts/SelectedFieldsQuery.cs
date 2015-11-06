using System;
using System.Collections.Generic;
using System.Linq;
using Foundatio.Repositories;
using Nest;

namespace Foundatio.Elasticsearch.Repositories.Queries {
    public interface ISelectedFieldsQuery {
        List<string> SelectedFields { get; }
    }

    public class SelectedFieldsQueryBuilder : QueryBuilderBase {
        public override void BuildSearch<T>(IReadOnlyRepository<T> repository, SearchDescriptor<T> descriptor, object query) {
            var selectedFieldsQuery = query as ISelectedFieldsQuery;
            if (selectedFieldsQuery == null)
                return;

            var elasticRepo = repository as ElasticReadOnlyRepositoryBase<T>;
            if (selectedFieldsQuery.SelectedFields.Count > 0)
                descriptor.Source(s => s.Include(selectedFieldsQuery.SelectedFields.ToArray()));
            else if (elasticRepo?.DefaultExcludes.Length > 0)
                descriptor.Source(s => s.Exclude(elasticRepo.DefaultExcludes));
        }
    }

    public static class SelectedFieldsQueryExtensions {
        public static T WithSelectedField<T>(this T query, string field) where T : ISelectedFieldsQuery {
            query.SelectedFields.Add(field);
            return query;
        }

        public static T WithSelectedFields<T>(this T query, params string[] fields) where T : ISelectedFieldsQuery {
            query.SelectedFields.AddRange(fields.Distinct());
            return query;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace Foundatio.Elasticsearch.Repositories.Queries {
    public interface ISelectedFieldsQuery {
        List<string> SelectedFields { get; }
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

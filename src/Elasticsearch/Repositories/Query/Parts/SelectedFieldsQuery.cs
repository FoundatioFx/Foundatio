using System;
using System.Collections.Generic;

namespace Foundatio.Elasticsearch.Repositories.Queries {
    public interface ISelectedFieldsQuery {
        List<string> SelectedFields { get; }
    }

    public static class SelectedFieldsQueryExtensions {
        public static T WithSelectedField<T>(this T query, string field) where T : ISelectedFieldsQuery {
            query.SelectedFields.Add(field);
            return query;
        }
    }
}

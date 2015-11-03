using System;

namespace Foundatio.Elasticsearch.Repositories.Queries {
    public interface ITypeQuery {
        string Type { get; set; }
    }

    public static class TypeQueryExtensions {
        public static T WithType<T>(this T query, string type) where T : ITypeQuery {
            query.Type = type;
            return query;
        }
    }
}

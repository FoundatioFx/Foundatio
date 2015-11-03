using System;

namespace Foundatio.Elasticsearch.Repositories.Queries {
    public interface ISoftDeletesQuery {
        bool IncludeSoftDeletes { get; set; }
    }

    public static class SoftDeletesQueryExtensions {
        public static T IncludeDeleted<T>(this T query, bool includeDeleted = true) where T : ISoftDeletesQuery {
            query.IncludeSoftDeletes = includeDeleted;
            return query;
        }
    }
}

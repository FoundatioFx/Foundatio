using System;

namespace Foundatio.Elasticsearch.Repositories {
    public interface IParentQuery {
        ITypeQuery ParentQuery { get; set; }
    }

    public static class ParentQueryExtensions {
        public static TQuery WithParentQuery<TQuery, TParentQuery>(this TQuery query, Func<TParentQuery, TParentQuery> parentQueryFunc) where TQuery : IParentQuery where TParentQuery : class, ITypeQuery, new() {
            if (parentQueryFunc == null)
                throw new ArgumentNullException(nameof(parentQueryFunc));

            var parentQuery = query.ParentQuery as TParentQuery ?? new TParentQuery();
            query.ParentQuery = parentQueryFunc(parentQuery);

            return query;
        }

        public static Query WithParentQuery<T>(this Query query, Func<T, T> parentQueryFunc) where T : class, ITypeQuery, new() {
            if (parentQueryFunc == null)
                throw new ArgumentNullException(nameof(parentQueryFunc));

            var parentQuery = query.ParentQuery as T ?? new T();
            query.ParentQuery = parentQueryFunc(parentQuery);

            return query;
        }

        public static ElasticQuery WithParentQuery<T>(this ElasticQuery query, Func<T, T> parentQueryFunc) where T : class, ITypeQuery, new() {
            if (parentQueryFunc == null)
                throw new ArgumentNullException(nameof(parentQueryFunc));

            var parentQuery = query.ParentQuery as T ?? new T();
            query.ParentQuery = parentQueryFunc(parentQuery);

            return query;
        }

        public static ElasticQuery WithParentQuery(this ElasticQuery query, Func<ParentQuery, ParentQuery> parentQueryFunc) {
            if (parentQueryFunc == null)
                throw new ArgumentNullException(nameof(parentQueryFunc));

            var parentQuery = query.ParentQuery as ParentQuery ?? new ParentQuery();
            query.ParentQuery = parentQueryFunc(parentQuery);

            return query;
        }
    }
}

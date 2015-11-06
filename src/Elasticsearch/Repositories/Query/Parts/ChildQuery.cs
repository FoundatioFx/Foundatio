using System;
using Foundatio.Repositories;
using Nest;

namespace Foundatio.Elasticsearch.Repositories.Queries {
    public interface IChildQuery {
        ITypeQuery ChildQuery { get; set; }
    }

    public class ChildQueryBuilder : QueryBuilderBase {
        public override void BuildFilter<T>(IReadOnlyRepository<T> repository, FilterContainer container, object query) {
        }
    }

    public static class ChildQueryExtensions {
        public static TQuery WithChildQuery<TQuery, TChildQuery>(this TQuery query, Func<TChildQuery, TChildQuery> childQueryFunc) where TQuery : IChildQuery where TChildQuery : class, ITypeQuery, new() {
            if (childQueryFunc == null)
                throw new ArgumentNullException(nameof(childQueryFunc));

            var childQuery = query.ChildQuery as TChildQuery ?? new TChildQuery();
            query.ChildQuery = childQueryFunc(childQuery);

            return query;
        }

        public static Query WithChildQuery<T>(this Query query, Func<T, T> childQueryFunc) where T : class, ITypeQuery, new() {
            if (childQueryFunc == null)
                throw new ArgumentNullException(nameof(childQueryFunc));

            var childQuery = query.ChildQuery as T ?? new T();
            query.ChildQuery = childQueryFunc(childQuery);

            return query;
        }

        public static ElasticQuery WithChildQuery<T>(this ElasticQuery query, Func<T, T> childQueryFunc) where T : class, ITypeQuery, new() {
            if (childQueryFunc == null)
                throw new ArgumentNullException(nameof(childQueryFunc));

            var childQuery = query.ChildQuery as T ?? new T();
            query.ChildQuery = childQueryFunc(childQuery);

            return query;
        }

        public static ElasticQuery WithChildQuery(this ElasticQuery query, Func<ChildQuery, ChildQuery> childQueryFunc) {
            if (childQueryFunc == null)
                throw new ArgumentNullException(nameof(childQueryFunc));

            var childQuery = query.ChildQuery as ChildQuery ?? new ChildQuery();
            query.ChildQuery = childQueryFunc(childQuery);

            return query;
        }
    }
}

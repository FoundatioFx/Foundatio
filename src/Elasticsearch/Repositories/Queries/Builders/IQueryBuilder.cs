using System;
using Nest;

namespace Foundatio.Elasticsearch.Repositories.Queries.Builders {
    public interface IQueryBuilder {
        void BuildQuery(object query, object options, QueryContainer container);
        void BuildFilter(object query, object options, FilterContainer container);
        void BuildSearch<T>(object query, object options, SearchDescriptor<T> descriptor) where T : class, new();
    }

    public abstract class QueryBuilderBase : IQueryBuilder {
        public virtual void BuildQuery(object query, object options, QueryContainer container) { }
        public virtual void BuildFilter(object query, object options, FilterContainer container) { }
        public virtual void BuildSearch<T>(object query, object options, SearchDescriptor<T> descriptor) where T : class, new() { }
    }
}
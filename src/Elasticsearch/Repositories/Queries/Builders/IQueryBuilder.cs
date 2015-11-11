using System;
using Nest;

namespace Foundatio.Elasticsearch.Repositories.Queries.Builders {
    public interface IQueryBuilder {
        void BuildQuery<T>(object query, object options, ref QueryContainer container) where T : class, new();
        void BuildFilter<T>(object query, object options, ref FilterContainer container) where T : class, new();
        void BuildSearch<T>(object query, object options, ref SearchDescriptor<T> descriptor) where T : class, new();
    }

    public abstract class QueryBuilderBase : IQueryBuilder {
        public virtual void BuildQuery<T>(object query, object options, ref QueryContainer container) where T : class, new() { }
        public virtual void BuildFilter<T>(object query, object options, ref FilterContainer container) where T : class, new() { }
        public virtual void BuildSearch<T>(object query, object options, ref SearchDescriptor<T> descriptor) where T : class, new() { }
    }
}
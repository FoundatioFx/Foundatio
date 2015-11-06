using Foundatio.Repositories;
using Nest;

namespace Foundatio.Elasticsearch.Repositories.Queries {
    public interface IQueryBuilder {
        void BuildQuery<T>(IReadOnlyRepository<T> repository, QueryContainer container, object query) where T : class, new();
        void BuildFilter<T>(IReadOnlyRepository<T> repository, FilterContainer container, object query) where T : class, new();
        void BuildSearch<T>(IReadOnlyRepository<T> repository, SearchDescriptor<T> descriptor, object query) where T : class, new();
    }

    public abstract class QueryBuilderBase : IQueryBuilder {
        public virtual void BuildQuery<T>(IReadOnlyRepository<T> repository, QueryContainer container, object query) where T : class, new() { }
        public virtual void BuildFilter<T>(IReadOnlyRepository<T> repository, FilterContainer container, object query) where T : class, new() { }
        public virtual void BuildSearch<T>(IReadOnlyRepository<T> repository, SearchDescriptor<T> descriptor, object query) where T : class, new() { }
    }
}
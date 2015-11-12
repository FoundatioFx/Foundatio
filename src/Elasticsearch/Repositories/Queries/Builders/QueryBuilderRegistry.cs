using System;
using System.Collections.Generic;
using Nest;

namespace Foundatio.Elasticsearch.Repositories.Queries.Builders {
    public class QueryBuilderRegistry {
        private readonly List<IQueryBuilder> _builders = new List<IQueryBuilder>();

        public void Register(IQueryBuilder builder) {
            _builders.Add(builder);
        }

        public void RegisterDefaults() {
            _builders.Add(new PagableQueryBuilder());
            _builders.Add(new SelectedFieldsQueryBuilder());
            _builders.Add(new SortableQueryBuilder());
            _builders.Add(new FacetQueryBuilder());
            _builders.Add(new ParentQueryBuilder(this));
            _builders.Add(new ChildQueryBuilder(this));
            _builders.Add(new IdentityQueryBuilder());
            _builders.Add(new SoftDeletesQueryBuilder());
            _builders.Add(new DateRangeQueryBuilder());
            _builders.Add(new SearchQueryBuilder());
            _builders.Add(new ElasticFilterQueryBuilder());
            _builders.Add(new FieldConditionsQueryBuilder());
        }

        public QueryContainer BuildQuery<T>(object query, object options = null, QueryContainer container = null) where T : class, new() {
            container &= new FilteredQuery { Filter = BuildFilter<T>(query, options) };

            foreach (var builder in _builders)
                builder.BuildQuery<T>(query, options, ref container);

            return container;
        }

        public FilterContainer BuildFilter<T>(object query, object options = null, FilterContainer container = null) where T : class, new() {
            if (container == null)
                container = new MatchAllFilter();

            foreach (var builder in _builders)
                builder.BuildFilter<T>(query, options, ref container);

            return container;
        }

        public SearchDescriptor<T> BuildSearch<T>(object query, object options = null, SearchDescriptor<T> descriptor = null) where T : class, new() {
            if (descriptor == null)
                descriptor = new SearchDescriptor<T>();

            foreach (var builder in _builders)
                builder.BuildSearch(query, options, ref descriptor);

            return descriptor;
        }
    }
}
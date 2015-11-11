using System;
using System.Linq;
using Foundatio.Elasticsearch.Repositories.Queries.Options;
using Nest;

namespace Foundatio.Elasticsearch.Repositories.Queries.Builders {
    public class FacetQueryBuilder : QueryBuilderBase {
        public override void BuildSearch<T>(object query, object options, ref SearchDescriptor<T> descriptor) {
            var facetQuery = query as IFacetQuery;
            if (facetQuery?.FacetFields == null || facetQuery.FacetFields.Count <= 0)
                return;

            var opt = options as IQueryOptions;
            if (opt?.AllowedFacetFields?.Length > 0 && !facetQuery.FacetFields.All(f => opt.AllowedFacetFields.Contains(f.Field)))
                throw new InvalidOperationException("All facet fields must be allowed.");

            descriptor.Aggregations(agg => GetAggregationDescriptor<T>(facetQuery));
        }

        private AggregationDescriptor<T> GetAggregationDescriptor<T>(object query) where T : class {
            var facetQuery = query as IFacetQuery;
            if (facetQuery == null || facetQuery.FacetFields.Count == 0)
                return null;

            var descriptor = new AggregationDescriptor<T>();
            foreach (var t in facetQuery.FacetFields)
                descriptor = descriptor.Terms(t.Field, s => s.Field(t.Field).Size(t.Size ?? 100));

            return descriptor;
        }
    }
}
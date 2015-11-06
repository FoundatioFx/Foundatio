using System;
using System.Collections.Generic;
using System.Linq;
using Foundatio.Repositories;
using Foundatio.Repositories.Queries;
using Nest;

namespace Foundatio.Elasticsearch.Repositories.Queries {
    public interface IFacetQuery {
        List<FacetField> FacetFields { get; }
    }

    public class FacetQueryBuilder : QueryBuilderBase {
        public override void BuildSearch<T>(IReadOnlyRepository<T> repository, SearchDescriptor<T> descriptor, object query) {
            var facetQuery = query as IFacetQuery;
            if (facetQuery == null || facetQuery.FacetFields.Count <= 0)
                return;

            var elasticRepo = repository as ElasticReadOnlyRepositoryBase<T>;
            if (elasticRepo != null && elasticRepo.AllowedFacetFields.Length > 0 && !facetQuery.FacetFields.All(f => elasticRepo.AllowedFacetFields.Contains(f.Field)))
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

    public static class FacetQueryExtensions {
        public static T WithFacet<T>(this T query, string field, int? maxTerms = null) where T : IFacetQuery {
            if (!String.IsNullOrEmpty(field))
                query.FacetFields.Add(new FacetField { Field = field, Size = maxTerms });

            return query;
        }

        public static T WithFacets<T>(this T query, params string[] fields) where T : IFacetQuery {
            if (fields.Length > 0)
                query.FacetFields.AddRange(fields.Select(f => new FacetField { Field = f }));
            return query;
        }

        public static T WithFacets<T>(this T query, int maxTerms, params string[] fields) where T : IFacetQuery {
            if (fields.Length > 0)
                query.FacetFields.AddRange(fields.Select(f => new FacetField { Field = f, Size = maxTerms }));
            return query;
        }

        public static T WithFacets<T>(this T query, FacetOptions facets) where T : IFacetQuery {
            if (facets != null)
                query.FacetFields.AddRange(facets.Fields);
            return query;
        }
    }
}

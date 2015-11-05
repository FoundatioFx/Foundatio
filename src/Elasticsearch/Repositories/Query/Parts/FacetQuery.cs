﻿using System;
using System.Collections.Generic;
using System.Linq;
using Foundatio.Repositories.Queries;
using Nest;

namespace Foundatio.Elasticsearch.Repositories.Queries {
    public interface IFacetQuery {
        List<FacetField> FacetFields { get; }
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

        public static AggregationDescriptor<T> GetAggregationDescriptor<T>(this IFacetQuery query) where T : class {
            if (query.FacetFields.Count == 0)
                return null;

            var descriptor = new AggregationDescriptor<T>();
            foreach (var t in query.FacetFields)
                descriptor = descriptor.Terms(t.Field, s => s.Field(t.Field).Size(t.Size ?? 100));

            return descriptor;
        }
    }
}

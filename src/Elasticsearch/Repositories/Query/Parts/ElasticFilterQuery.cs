using System;
using System.Collections.Generic;
using Nest;

namespace Foundatio.Elasticsearch.Repositories.Queries {
    public interface IElasticFilterQuery {
        FilterContainer ElasticFilter { get; set; }
    }

    public static class ElasticFilterQueryExtensions {
        public static T WithElasticFilter<T>(this T query, FilterContainer filter) where T : IElasticFilterQuery {
            query.ElasticFilter = filter;
            return query;
        }
    }

    public interface IElasticIndicesQuery {
        List<string> Indices { get; set; }
    }

    public static class ElasticFilterIndicesExtensions {
        public static T WithIndices<T>(this T query, params string[] indices) where T : IElasticIndicesQuery {
            if (query.Indices == null)
                query.Indices = new List<String>();

            query.Indices.AddRange(indices);
            return query;
        }

        public static T WithIndice<T>(this T query, string index) where T : IElasticIndicesQuery {
            query.Indices.Add(index);
            return query;
        }

        public static T WithIndices<T>(this T query, IEnumerable<string> indices) where T : IElasticIndicesQuery {
            query.Indices.AddRange(indices);
            return query;
        }

        public static T WithIndices<T>(this T query, DateTime? utcStart, DateTime? utcEnd, string nameFormat = null) where T : IElasticIndicesQuery {
            query.Indices.AddRange(GetTargetIndex<T>(utcStart, utcEnd, nameFormat));
            return query;
        }

        private static IEnumerable<string> GetTargetIndex<T>(DateTime? utcStart, DateTime? utcEnd, string nameFormat = null) {
            if (!utcStart.HasValue)
                utcStart = DateTime.UtcNow;

            if (!utcEnd.HasValue || utcEnd.Value < utcStart)
                utcEnd = DateTime.UtcNow;

            if (String.IsNullOrEmpty(nameFormat))
                nameFormat = $"'{typeof(T).Name.ToLower()}-'yyyy.MM.dd";

            // Use the end of the day as we are using daily indexes.
            var utcEndOfDay = utcEnd.Value.EndOfDay();

            var indices = new List<string>();
            for (DateTime current = utcStart.Value; current <= utcEndOfDay; current = current.AddDays(1))
                indices.Add(current.ToString(nameFormat));

            return indices;
        }
    }
}

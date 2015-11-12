using System;
using System.Collections.Generic;
using Foundatio.Elasticsearch.Extensions;

namespace Foundatio.Elasticsearch.Repositories.Queries {
    public interface IElasticIndicesQuery {
        List<string> Indices { get; set; }
    }
    
    public static class ElasticFilterIndicesExtensions {
        public static T WithIndice<T>(this T query, string index) where T : IElasticIndicesQuery {
            query.Indices?.Add(index);
            return query;
        }

        public static T WithIndices<T>(this T query, params string[] indices) where T : IElasticIndicesQuery {
            query.Indices?.AddRange(indices);
            return query;
        }
        
        public static T WithIndices<T>(this T query, IEnumerable<string> indices) where T : IElasticIndicesQuery {
            query.Indices?.AddRange(indices);
            return query;
        }

        public static T WithIndices<T>(this T query, DateTime? utcStart, DateTime? utcEnd, string nameFormat) where T : IElasticIndicesQuery {
            if (String.IsNullOrEmpty(nameFormat))
                throw new ArgumentNullException(nameof(nameFormat));
            
            var range = new DateRange { StartDate = utcStart, EndDate =  utcEnd };
            if (range.UseStartDate && range.UseEndDate)
                query.Indices?.AddRange(GetTargetIndex(range.GetStartDate(), range.GetEndDate(), nameFormat));

            return query;
        }

        private static IEnumerable<string> GetTargetIndex(DateTime utcStart, DateTime utcEnd, string nameFormat) {
            // Use the end of the day as we are using daily indexes.
            var utcEndOfDay = utcEnd.EndOfDay();

            var indices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (DateTime current = utcStart; current <= utcEndOfDay; current = current.AddDays(1))
                indices.Add(current.ToString(nameFormat));

            return indices;
        }
    }
}
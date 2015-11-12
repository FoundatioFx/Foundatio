using System;
using System.Collections.Generic;
using System.Linq;
using Foundatio.Elasticsearch.Repositories.Queries;

namespace Foundatio.Elasticsearch.Tests.Repositories.Queries {
    public interface IAgeQuery {
        List<int> Ages { get; set; }
    }

    public static class AgeQueryExtensions {
        public static T WithAge<T>(this T query, int age) where T : IAgeQuery {
            query.Ages?.Add(age);
            return query;
        }

        public static T WithAgeRange<T>(this T query, int minAge, int maxAge) where T : IAgeQuery {
            query.Ages?.AddRange(Enumerable.Range(minAge, maxAge - minAge + 1));
            return query;
        }
    }

    public class AgeQuery : ElasticQuery, IAgeQuery {
        public AgeQuery() {
            Ages = new List<int>();
        }

        public List<int> Ages { get; set; }
    }
}
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Elasticsearch.Configuration;
using Foundatio.Repositories.Models;
using Nest;

namespace Foundatio.Elasticsearch.Extensions {
    public static class ElasticIndexExtensions {
        public static IEnumerable<KeyValuePair<Type, string>> ToTypeIndices(this IEnumerable<IElasticIndex> indexes) {
            return indexes.SelectMany(idx => idx.GetIndexTypes().Select(kvp => new KeyValuePair<Type, string>(kvp.Key, idx.AliasName)));
        }

        public static IDictionary<Type, string> ToIndexTypeNames(this IEnumerable<IElasticIndex> indexes) {
            return indexes.SelectMany(idx => idx.GetIndexTypes()).ToDictionary(k => k.Key, k => k.Value.Name);
        }

        public static ICollection<FacetResult> ToFacetResults<T>(this ISearchResponse<T> res) where T : class {
            var result = new List<FacetResult>();
            if (res.Aggregations == null || res.Aggregations.Count == 0)
                return result;

            foreach (var key in res.Aggregations.Keys) {
                var bucket = res.Aggregations[key] as Bucket;

                if (bucket == null)
                    continue;

                result.Add(new FacetResult {
                    Field = key,
                    Terms = new NumberDictionary(bucket.Items.OfType<KeyItem>().ToDictionary(t => t.Key, t => t.DocCount))
                });
            }

            return result;
        }

        public static IBulkResponse IndexMany<T>(this IElasticClient client, IEnumerable<T> objects, Func<T, string> getParent, Func<T, string> getIndex = null, string type = null) where T : class {
            if (getParent == null)
                return client.IndexMany(objects, null, type);

            var indexBulkRequest = CreateIndexBulkRequest(objects, getIndex, type, getParent);
            return client.Bulk((IBulkRequest)indexBulkRequest);
        }

        public static Task<IBulkResponse> IndexManyAsync<T>(this IElasticClient client, IEnumerable<T> objects, Func<T, string> getParent, Func<T, string> getIndex = null, string type = null) where T : class {
            if (getParent == null && getIndex == null)
                return client.IndexManyAsync(objects, null, type);

            var indexBulkRequest = CreateIndexBulkRequest(objects, getIndex, type, getParent);
            return client.BulkAsync(indexBulkRequest);
        }

        private static BulkRequest CreateIndexBulkRequest<T>(IEnumerable<T> objects, Func<T, string> getIndex, string type, Func<T, string> getParent) where T : class {
            if (objects == null)
                throw new ArgumentNullException(nameof(objects));

            var bulkRequest = new BulkRequest();
            TypeNameMarker typeNameMarker = type;
            bulkRequest.Type = typeNameMarker;
            var list = objects.Select(o => new BulkIndexOperation<T>(o) {
                Parent = getParent?.Invoke(o),
                Index = getIndex?.Invoke(o),
            }).Cast<IBulkOperation>().ToList();
            bulkRequest.Operations = list;

            return bulkRequest;
        }

        public static ObjectMappingDescriptor<TParent, TChild> RootPath<TParent, TChild>(this ObjectMappingDescriptor<TParent, TChild> t) where TParent : class where TChild : class {
            return t.Path("just_name");
        }
    }
}
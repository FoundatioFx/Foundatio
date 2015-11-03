using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Elasticsearch.Net.ConnectionPool;
using Foundatio.Caching;
using Foundatio.Elasticsearch.Extensions;
using Foundatio.Elasticsearch.Jobs;
using Foundatio.Extensions;
using Foundatio.Jobs;
using Foundatio.Lock;
using Foundatio.Queues;
using Nest;

namespace Foundatio.Elasticsearch.Configuration {
    public abstract class ElasticsearchConfigurationBase {
        protected readonly IQueue<WorkItemData> _workItemQueue;
        protected readonly ILockProvider _lockProvider;
        protected IDictionary<Type, string> _indexMap;

        public ElasticsearchConfigurationBase(IQueue<WorkItemData> workItemQueue, ICacheClient cacheClient) {
            _workItemQueue = workItemQueue;
            _lockProvider = new ThrottlingLockProvider(cacheClient, 1, TimeSpan.FromMinutes(1));
        }

        public virtual IElasticClient GetClient(IEnumerable<Uri> serverUris) {
            var indexes = GetIndexes().ToList();
            _indexMap = indexes.SelectMany(idx => idx.GetIndexTypes()).ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Name);
            
            var client = new ElasticClient(GetConnectionSettings(serverUris, indexes));
            ConfigureIndexes(client, indexes);
            return client;
        }

        protected virtual ConnectionSettings GetConnectionSettings(IEnumerable<Uri> serverUris, IEnumerable<IElasticsearchIndex> indexes) {
            var connectionPool = new StaticConnectionPool(serverUris);
            return new ConnectionSettings(connectionPool)
                .MapDefaultTypeIndices(t => t.AddRange(indexes.ToTypeIndices()))
                .MapDefaultTypeNames(t => {
                    t.AddRange(indexes.SelectMany(idx => idx.GetIndexTypes().ToDictionary(k => k.Key, k => k.Value.Name)));
                });
        }

        protected virtual void ConfigureIndexes(IElasticClient client, IEnumerable<IElasticsearchIndex> indexes) {
            foreach (var index in indexes) {
                var idx = index;
                int currentVersion = GetAliasVersion(client, idx.AliasName);
                bool newIndexExists = client.IndexExists(idx.VersionedName).Exists;

                if (!newIndexExists)
                    client.CreateIndex(idx.VersionedName, idx.CreateIndex);

                if (!client.AliasExists(idx.AliasName).Exists)
                    client.Alias(a => a
                        .Add(add => add
                            .Index(idx.VersionedName)
                            .Alias(idx.AliasName)
                        )
                    );

                // already on current version
                if (currentVersion >= idx.Version || currentVersion < 1)
                    continue;

                var reindexWorkItem = new ReindexWorkItem {
                    OldIndex = String.Concat(idx.AliasName, "-v", currentVersion),
                    NewIndex = idx.VersionedName,
                    Alias = idx.AliasName,
                    DeleteOld = true,
                    ParentMaps = idx.GetIndexTypes()
                            .Select(kvp => new ParentMap {Type = kvp.Value.Name, ParentPath = kvp.Value.ParentPath})
                            .Where(m => !String.IsNullOrEmpty(m.ParentPath))
                            .ToList()
                };

                bool isReindexing = _lockProvider.IsLockedAsync(String.Concat("reindex:", reindexWorkItem.Alias, reindexWorkItem.OldIndex, reindexWorkItem.NewIndex)).Result;
                // already reindexing
                if (isReindexing)
                    continue;

                // enqueue reindex to new version
                _lockProvider.TryUsingAsync("enqueue-reindex", () => _workItemQueue.EnqueueAsync(reindexWorkItem), TimeSpan.Zero, CancellationToken.None).Wait();
            }
        }

        protected string GetIndexAliasForType(Type entityType) {
            return _indexMap.ContainsKey(entityType) ? _indexMap[entityType] : null;
        }

        protected abstract IEnumerable<IElasticsearchIndex> GetIndexes();

        protected virtual int GetAliasVersion(IElasticClient client, string alias) {
            var res = client.GetAlias(a => a.Alias(alias));
            if (!res.Indices.Any())
                return -1;

            string indexName = res.Indices.FirstOrDefault().Key;
            string versionString = indexName.Substring(indexName.LastIndexOf("-", StringComparison.Ordinal));

            int version;
            if (!Int32.TryParse(versionString.Substring(2), out version))
                return -1;

            return version;
        }
    }
}

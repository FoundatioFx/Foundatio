using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Elasticsearch.Net.ConnectionPool;
using Foundatio.Caching;
using Foundatio.Elasticsearch.Extensions;
using Foundatio.Extensions;
using Foundatio.Jobs;
using Foundatio.Lock;
using Foundatio.Queues;
using Nest;

namespace Foundatio.Elasticsearch.Configuration {
    public class ElasticsearchConfiguration {
        private readonly IQueue<WorkItemData> _workItemQueue;
        private readonly ILockProvider _lockProvider;
        private IDictionary<Type, string> _indexMap;

        public ElasticsearchConfiguration(IQueue<WorkItemData> workItemQueue, ICacheClient cacheClient) {
            _workItemQueue = workItemQueue;
            _lockProvider = new ThrottlingLockProvider(cacheClient, 1, TimeSpan.FromMinutes(1));
        }

        public IElasticClient GetClient(IEnumerable<Uri> serverUris) {
            var connectionPool = new StaticConnectionPool(serverUris);
            var indexes = GetIndexes().ToList();
            _indexMap = indexes.SelectMany(idx => idx.GetIndexTypes()).ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Name);
            var settings = new ConnectionSettings(connectionPool)
                .MapDefaultTypeIndices(t => t.AddRange(indexes.ToTypeIndices()))
                .MapDefaultTypeNames(t => {
                    t.AddRange(indexes.SelectMany(idx => idx.GetIndexTypes().ToDictionary(k => k.Key, k => k.Value.Name)));
                })
                .SetDefaultTypeNameInferrer(p => p.Name.ToLowerUnderscoredWords())
                .SetDefaultPropertyNameInferrer(p => p.ToLowerUnderscoredWords());
            var client = new ElasticClient(settings, new KeepAliveHttpConnection(settings));

            ConfigureIndexes(client);

            return client;
        }

        public void ConfigureIndexes(IElasticClient client) {
            var indexes = GetIndexes();
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

            // TODO: Move this to a one time work item.
            // move activity to contact index
            int activityVersion = GetAliasVersion(client, Settings.Current.AppScopePrefix + "activity");
            if (activityVersion > 0) {
                var index = GetIndexes().First(i => i.AliasName == ContactIndex.Alias);
                _workItemQueue.EnqueueAsync(new ReindexWorkItem {
                    OldIndex = String.Concat(Settings.Current.AppScopePrefix + "activity", "-v", activityVersion),
                    NewIndex = index.VersionedName,
                    DeleteOld = true,
                    ParentMaps = { new ParentMap { Type = "activity", ParentPath = "contact_id" } }
                }).Wait();
            }
        }

        public string GetIndexAliasForType(Type entityType) {
            return _indexMap.ContainsKey(entityType) ? _indexMap[entityType] : null;
        }

        public IEnumerable<IElasticsearchIndex> GetIndexes() {
            return new IElasticsearchIndex[] {
                new OrganizationIndex(),
                new ContactIndex()
            };
        }

        public int GetAliasVersion(IElasticClient client, string alias) {
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

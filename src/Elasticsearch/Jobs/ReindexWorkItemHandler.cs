using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Elasticsearch.Net;
using Foundatio.Elasticsearch.Extensions;
using Foundatio.Extensions;
using Foundatio.Jobs;
using Foundatio.Lock;
using Nest;
using Newtonsoft.Json.Linq;
using Foundatio.Logging;
using Newtonsoft.Json;

namespace Foundatio.Elasticsearch.Jobs {
    public class ReindexWorkItemHandler : WorkItemHandlerBase {
        private readonly IElasticClient _client;
        private readonly ILockProvider _lockProvider;

        public ReindexWorkItemHandler(IElasticClient client, ILockProvider lockProvider) {
            _client = client;
            _lockProvider = lockProvider;
        }

        public override Task<ILock> GetWorkItemLockAsync(object workItem, CancellationToken cancellationToken = default(CancellationToken)) {
            var reindexWorkItem = workItem as ReindexWorkItem;
            if (reindexWorkItem == null)
                return null;

            return _lockProvider.AcquireAsync(String.Concat("reindex:", reindexWorkItem.Alias, reindexWorkItem.OldIndex, reindexWorkItem.NewIndex), TimeSpan.FromMinutes(20), cancellationToken);
        }

        public override async Task HandleItemAsync(WorkItemContext context) {
            var workItem = context.GetData<ReindexWorkItem>();

            long existingDocCount = (await _client.CountAsync(d => d.Index(workItem.NewIndex)).AnyContext()).Count;
            Logger.Info().Message("Received reindex work item for new index {0}", workItem.NewIndex).Write();
            var startTime = DateTime.UtcNow.AddSeconds(-1);
            await context.ReportProgressAsync(0, "Starting reindex...").AnyContext();
            var result = await ReindexAsync(workItem, context, 0, 90, workItem.StartUtc).AnyContext();
            await context.ReportProgressAsync(90, $"Total: {result.Total} Completed: {result.Completed}").AnyContext();

            // TODO: Check to make sure the docs have been added to the new index before changing alias

            if (!String.IsNullOrEmpty(workItem.Alias)) {
                await _client.AliasAsync(x => x
                    .Remove(a => a.Alias(workItem.Alias).Index(workItem.OldIndex))
                    .Add(a => a.Alias(workItem.Alias).Index(workItem.NewIndex))).AnyContext();

                await context.ReportProgressAsync(98, $"Updated alias: {workItem.Alias} Remove: {workItem.OldIndex} Add: {workItem.NewIndex}").AnyContext();
            }

            await _client.RefreshAsync().AnyContext();
            var secondPassResult = await ReindexAsync(workItem, context, 90, 98, startTime).AnyContext();
            await context.ReportProgressAsync(98, $"Total: {secondPassResult.Total} Completed: {secondPassResult.Completed}").AnyContext();

            if (workItem.DeleteOld) {
                await _client.RefreshAsync().AnyContext();
                long newDocCount = (await _client.CountAsync(d => d.Index(workItem.NewIndex)).AnyContext()).Count - existingDocCount;
                long oldDocCount = (await _client.CountAsync(d => d.Index(workItem.OldIndex)).AnyContext()).Count;
                await context.ReportProgressAsync(98, $"Old Docs: {oldDocCount} New Docs: {newDocCount}").AnyContext();
                if (newDocCount >= oldDocCount)
                    await _client.DeleteIndexAsync(d => d.Index(workItem.OldIndex)).AnyContext();
                await context.ReportProgressAsync(98, $"Deleted index: {workItem.OldIndex}").AnyContext();
            }
            await context.ReportProgressAsync(100).AnyContext();
        }

        private async Task<ReindexResult> ReindexAsync(ReindexWorkItem workItem, WorkItemContext context, int startProgress = 0, int endProgress = 100, DateTime? startTime = null) {
            const int pageSize = 100;
            const string scroll = "5m";
            string timestampField = workItem.TimestampField ?? "_timestamp";

            long completed = 0;

            var scanResults = await _client.SearchAsync<JObject>(s => s
                .Index(workItem.OldIndex)
                .AllTypes()
                .Filter(f => startTime.HasValue
                    ? f.Range(r => r.OnField(timestampField).Greater(startTime.Value))
                    : f.MatchAll())
                .From(0).Take(pageSize)
                .SearchType(SearchType.Scan)
                .Scroll(scroll)).AnyContext();

            if (!scanResults.IsValid || scanResults.ScrollId == null) {
                Logger.Error().Message("Invalid search result: message={0}", scanResults.GetErrorMessage()).Write();
                return new ReindexResult();
            }

            long totalHits = scanResults.Total;

            var parentMap = workItem.ParentMaps?.ToDictionary(p => p.Type, p => p.ParentPath) ?? new Dictionary<string, string>();

            var results = await _client.ScrollAsync<JObject>(scroll, scanResults.ScrollId).AnyContext();
            while (results.Documents.Any()) {
                var bulkDescriptor = new BulkDescriptor();
                foreach (var hit in results.Hits) {
                    var h = hit;
                    // TODO: Add support for doing JObject based schema migrations
                    bulkDescriptor.Index<JObject>(idx => {
                        idx
                            .Index(workItem.NewIndex)
                            .Type(h.Type)
                            .Id(h.Id)
                            .Document(h.Source);

                        if (String.IsNullOrEmpty(h.Type))
                            Logger.Error().Message("Hit type empty. id={0}", h.Id).Write();

                        if (parentMap.ContainsKey(h.Type)) {
                            if (String.IsNullOrEmpty(parentMap[h.Type]))
                                Logger.Error().Message("Parent map has empty value. id={0} type={1}", h.Id, h.Type).Write();

                            var parentId = h.Source.SelectToken(parentMap[h.Type]);
                            if (!String.IsNullOrEmpty(parentId?.ToString()))
                                idx.Parent(parentId.ToString());
                            else
                                Logger.Error().Message("Unable to get parent id. id={0} path={1}", h.Id, parentMap[h.Type]).Write();
                        }

                        return idx;
                    });
                }

                var bulkResponse = await _client.BulkAsync(bulkDescriptor).AnyContext();
                if (!bulkResponse.IsValid) {
                    string message = $"Reindex bulk error: old={workItem.OldIndex} new={workItem.NewIndex} completed={completed} message={bulkResponse.GetErrorMessage()}";
                    Logger.Warn().Message(message).Write();
                    // try each doc individually so we can see which doc is breaking us
                    foreach (var hit in results.Hits) {
                        var h = hit;
                        var response = await _client.IndexAsync<JObject>(h.Source, d => {
                            d
                                .Index(workItem.NewIndex)
                                .Type(h.Type)
                                .Id(h.Id);

                            if (parentMap.ContainsKey(h.Type)) {
                                var parentId = h.Source.SelectToken(parentMap[h.Type]);
                                if (!String.IsNullOrEmpty(parentId?.ToString()))
                                    d.Parent(parentId.ToString());
                                else
                                    Logger.Error().Message("Unable to get parent id. id={0} path={1}", h.Id, parentMap[h.Type]).Write();
                            }

                            return d;
                        }).AnyContext();

                        if (response.IsValid)
                            continue;

                        message = $"Reindex error: old={workItem.OldIndex} new={workItem.NewIndex} id={hit.Id} completed={completed} message={response.GetErrorMessage()}";
                        Logger.Error().Message(message).Write();

                        var errorDoc = new JObject(new {
                            h.Type,
                            Content = h.Source.ToString(Formatting.Indented)
                        });

                        if (parentMap.ContainsKey(h.Type)) {
                            var parentId = h.Source.SelectToken(parentMap[h.Type]);
                            if (!String.IsNullOrEmpty(parentId?.ToString()))
                                errorDoc["ParentId"] = parentId.ToString();
                            else
                                Logger.Error().Message("Unable to get parent id. id={0} path={1}", h.Id, parentMap[h.Type]).Write();
                        }

                        // put the document into an error index
                        response = await _client.IndexAsync<JObject>(errorDoc, d => {
                            d
                                .Index(workItem.NewIndex + "-error")
                                .Id(h.Id);

                            return d;
                        }).AnyContext();

                        if (response.IsValid)
                            continue;

                        throw new ReindexException(response.ConnectionStatus, message);
                    }
                }

                completed += bulkResponse.Items.Count();
                await context.ReportProgressAsync(CalculateProgress(totalHits, completed, startProgress, endProgress),
                    $"Total: {totalHits} Completed: {completed}").AnyContext();

                Logger.Info().Message($"Reindex Progress: {CalculateProgress(totalHits, completed, startProgress, endProgress)} Completed: {completed} Total: {totalHits}").Write();
                results = await _client.ScrollAsync<JObject>(scroll, results.ScrollId).AnyContext();
            }

            return new ReindexResult { Total = totalHits, Completed = completed };
        }

        private class ReindexResult {
            public long Total { get; set; }
            public long Completed { get; set; }
        }
    }
}

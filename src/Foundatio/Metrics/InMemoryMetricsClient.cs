using System;
using Foundatio.Caching;

namespace Foundatio.Metrics {
    public class InMemoryMetricsClient : CacheBucketMetricsClientBase {
        public InMemoryMetricsClient(InMemoryMetricsClientOptions options) : base(new InMemoryCacheClient(new InMemoryCacheClientOptions { LoggerFactory = options?.LoggerFactory }),  options) { }

        public override void Dispose() {
            base.Dispose();
            _cache.Dispose();
        }
    }
}

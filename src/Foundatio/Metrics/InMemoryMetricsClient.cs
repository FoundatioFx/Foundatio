using System;
using Foundatio.Caching;
using Foundatio.Logging;

namespace Foundatio.Metrics {
    public class InMemoryMetricsClient : CacheBucketMetricsClientBase {
        public InMemoryMetricsClient(bool buffered = true, string prefix = null, ILoggerFactory loggerFactory = null) : base(new InMemoryCacheClient(loggerFactory), buffered, prefix, loggerFactory) { }

        public override void Dispose() {
            base.Dispose();
            _cache.Dispose();
        }
    }
}

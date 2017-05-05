using System;
using Foundatio.Caching;
using Foundatio.Logging;

namespace Foundatio.Metrics {
    public class InMemoryMetricsClient : CacheBucketMetricsClientBase {
        [Obsolete("Use the options overload")]
        public InMemoryMetricsClient(bool buffered = true, string prefix = null, ILoggerFactory loggerFactory = null)
            : this(new InMemoryMetricsClientOptions { Buffered = buffered, Prefix = prefix, LoggerFactory = loggerFactory }) { }

        public InMemoryMetricsClient(InMemoryMetricsClientOptions options) : base(new InMemoryCacheClient(new InMemoryCacheClientOptions { LoggerFactory = options.LoggerFactory }),  options) { }

        public override void Dispose() {
            base.Dispose();
            _cache.Dispose();
        }
    }
}

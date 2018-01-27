using System;
using Foundatio.Caching;

namespace Foundatio.Metrics {
    public class InMemoryMetricsClient : CacheBucketMetricsClientBase {
        public InMemoryMetricsClient(InMemoryMetricsClientOptions options) : base(new InMemoryCacheClient(new InMemoryCacheClientOptions { LoggerFactory = options?.LoggerFactory }),  options) { }

        public InMemoryMetricsClient(Action<InMemoryMetricsClientOptions> config) : this(ConfigureOptions(config)) { }

        private static InMemoryMetricsClientOptions ConfigureOptions(Action<InMemoryMetricsClientOptions> config) {
            var options = new InMemoryMetricsClientOptions();
            config?.Invoke(options);
            return options;
        }

        public override void Dispose() {
            base.Dispose();
            _cache.Dispose();
        }
    }
}

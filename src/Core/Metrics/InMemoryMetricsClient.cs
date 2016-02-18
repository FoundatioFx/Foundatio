using System;
using Foundatio.Caching;

namespace Foundatio.Metrics {
    public class InMemoryMetricsClient : CacheBucketMetricsClientBase {
        public InMemoryMetricsClient(bool buffered = true, string prefix = null) : base(new InMemoryCacheClient(), buffered, prefix) {}
    }
}

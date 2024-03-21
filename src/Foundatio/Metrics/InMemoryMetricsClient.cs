using Foundatio.Caching;

namespace Foundatio.Metrics;

public class InMemoryMetricsClient : CacheBucketMetricsClientBase
{
    public InMemoryMetricsClient() : this(o => o) { }

    public InMemoryMetricsClient(InMemoryMetricsClientOptions options)
        : base(new InMemoryCacheClient(o => o.LoggerFactory(options?.LoggerFactory)), options) { }

    public InMemoryMetricsClient(Builder<InMemoryMetricsClientOptionsBuilder, InMemoryMetricsClientOptions> config)
        : this(config(new InMemoryMetricsClientOptionsBuilder()).Build()) { }

    public override void Dispose()
    {
        base.Dispose();
        _cache.Dispose();
    }
}

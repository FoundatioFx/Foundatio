using System;
using Foundatio.Caching;

namespace Foundatio.Metrics
{
    public class RedisMetricsClient : CacheBucketMetricsClientBase
    {
        public RedisMetricsClient(RedisMetricsClientOptions options) : base(new RedisCacheClient(o => o.ConnectionMultiplexer(options.ConnectionMultiplexer).LoggerFactory(options.LoggerFactory)), options) { }

        public RedisMetricsClient(Builder<RedisMetricsClientOptionsBuilder, RedisMetricsClientOptions> config)
            : this(config(new RedisMetricsClientOptionsBuilder()).Build()) { }

        public override void Dispose()
        {
            base.Dispose();
            _cache.Dispose();
        }
    }
}

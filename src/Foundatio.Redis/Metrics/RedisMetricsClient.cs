using System;
using Foundatio.Caching;
using Microsoft.Extensions.Logging;

namespace Foundatio.Metrics {
    public class RedisMetricsClient : CacheBucketMetricsClientBase {
        public RedisMetricsClient(RedisMetricsClientOptions options) : base(new RedisCacheClient(new RedisCacheClientOptions { ConnectionMultiplexer = options.ConnectionMultiplexer, LoggerFactory = options.LoggerFactory }), options) { }

        public override void Dispose() {
            base.Dispose();
            _cache.Dispose();
        }
    }
}

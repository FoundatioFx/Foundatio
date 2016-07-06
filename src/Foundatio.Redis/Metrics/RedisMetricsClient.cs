using System;
using Foundatio.Caching;
using Foundatio.Logging;
using StackExchange.Redis;

namespace Foundatio.Metrics {
    public class RedisMetricsClient : CacheBucketMetricsClientBase {
        public RedisMetricsClient(ConnectionMultiplexer connection, bool buffered = true, string prefix = null, ILoggerFactory loggerFactory = null) : base(new RedisCacheClient(connection), buffered, prefix, loggerFactory) {}

        public override void Dispose() {
            base.Dispose();
            _cache.Dispose();
        }
    }
}

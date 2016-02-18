using System;
using Foundatio.Caching;
using Foundatio.Metrics;
using StackExchange.Redis;

namespace Foundatio.Redis.Metrics {
    public class RedisMetricsClient : CacheBucketMetricsClientBase {
        public RedisMetricsClient(ConnectionMultiplexer connection, bool buffered = true, string prefix = null) : base(new RedisCacheClient(connection), buffered, prefix) {}
    }
}

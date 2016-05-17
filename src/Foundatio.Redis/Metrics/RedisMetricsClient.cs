using System;
using Foundatio.Caching;
using Foundatio.Logging;
using Foundatio.Metrics;
using StackExchange.Redis;

namespace Foundatio.Redis.Metrics {
    public class RedisMetricsClient : CacheBucketMetricsClientBase {
        public RedisMetricsClient(ConnectionMultiplexer connection, bool buffered = true, string prefix = null, ILoggerFactory loggerFactory = null) : base(new RedisCacheClient(connection), buffered, prefix, loggerFactory) {}
    }
}

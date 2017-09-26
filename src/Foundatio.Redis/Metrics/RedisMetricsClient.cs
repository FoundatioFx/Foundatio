using System;
using Foundatio.Caching;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Foundatio.Metrics {
    public class RedisMetricsClient : CacheBucketMetricsClientBase {
        [Obsolete("Use the options overload")]
        public RedisMetricsClient(ConnectionMultiplexer connection, bool buffered = true, string prefix = null, ILoggerFactory loggerFactory = null)
            : this(new RedisMetricsClientOptions {
                ConnectionMultiplexer = connection,
                Buffered = buffered,
                Prefix = prefix,
                LoggerFactory = loggerFactory
            }) { }

        public RedisMetricsClient(RedisMetricsClientOptions options) : base(new RedisCacheClient(new RedisCacheClientOptions { ConnectionMultiplexer = options.ConnectionMultiplexer, LoggerFactory = options.LoggerFactory }), options) { }

        public override void Dispose() {
            base.Dispose();
            _cache.Dispose();
        }
    }
}

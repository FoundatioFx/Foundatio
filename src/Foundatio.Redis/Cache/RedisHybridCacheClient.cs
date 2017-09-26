using System;
using Foundatio.Messaging;
using Foundatio.Serializer;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Foundatio.Caching {
    public class RedisHybridCacheClient : HybridCacheClient {
        public RedisHybridCacheClient(ConnectionMultiplexer connectionMultiplexer, ISerializer serializer = null, ILoggerFactory loggerFactory = null)
            : base(new RedisCacheClient(new RedisCacheClientOptions { ConnectionMultiplexer = connectionMultiplexer, Serializer = serializer, LoggerFactory = loggerFactory }), new RedisMessageBus(new RedisMessageBusOptions { Subscriber = connectionMultiplexer.GetSubscriber(), Topic = "cache-messages", Serializer = serializer, LoggerFactory = loggerFactory }), loggerFactory) { }

        public override void Dispose() {
            base.Dispose();
            _distributedCache.Dispose();
            _messageBus.Dispose();
        }
    }
}

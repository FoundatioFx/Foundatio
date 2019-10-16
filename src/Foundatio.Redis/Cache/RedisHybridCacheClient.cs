using System;
using Foundatio.Messaging;
using Foundatio.Serializer;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Foundatio.Caching {
    public class RedisHybridCacheClient : HybridCacheClient {
        public RedisHybridCacheClient(RedisCacheClientOptions options, InMemoryCacheClientOptions localOptions = null, string redisChannelName = "cache-messages")
            : base(new RedisCacheClient(o => o
                .ConnectionMultiplexer(options.ConnectionMultiplexer)
                .Serializer(options.Serializer)
                .LoggerFactory(options.LoggerFactory)),
            new RedisMessageBus(o => o
                .Subscriber(options.ConnectionMultiplexer.GetSubscriber())
                .Topic(redisChannelName)
                .Serializer(options.Serializer)
                .LoggerFactory(options.LoggerFactory)), localOptions, options.LoggerFactory) { }

        public RedisHybridCacheClient(Builder<RedisCacheClientOptionsBuilder, RedisCacheClientOptions> config, Builder<InMemoryCacheClientOptionsBuilder, InMemoryCacheClientOptions> localConfig = null, string redisChannelName = "cache-messages")
            : this(config(new RedisCacheClientOptionsBuilder()).Build(), localConfig(new InMemoryCacheClientOptionsBuilder()).Build(), redisChannelName) { }

        public override void Dispose() {
            base.Dispose();
            _distributedCache.Dispose();
            _messageBus.Dispose();
        }
    }
}

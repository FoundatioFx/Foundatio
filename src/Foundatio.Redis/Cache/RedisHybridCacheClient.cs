using System;
using Foundatio.Messaging;
using Foundatio.Serializer;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Foundatio.Caching {

    public class RedisHybridCacheClient : HybridCacheClient {
        public RedisHybridCacheClient(RedisHybridCacheClientOptions options, InMemoryCacheClientOptions localOptions = null)
            : base(new RedisCacheClient(o => o
                .ConnectionMultiplexer(options.ConnectionMultiplexer)
                .Serializer(options.Serializer)
                .LoggerFactory(options.LoggerFactory)),
            new RedisMessageBus(o => o
                .Subscriber(options.ConnectionMultiplexer.GetSubscriber())
                .Topic(options.RedisChannelName)
                .Serializer(options.Serializer)
                .LoggerFactory(options.LoggerFactory)), localOptions, options.LoggerFactory) { }

        public RedisHybridCacheClient(Builder<RedisHybridCacheClientOptionsBuilder, RedisHybridCacheClientOptions> config, Builder<InMemoryCacheClientOptionsBuilder, InMemoryCacheClientOptions> localConfig = null)
            : this(config(new RedisHybridCacheClientOptionsBuilder()).Build(), localConfig(new InMemoryCacheClientOptionsBuilder()).Build()) { }

        public override void Dispose() {
            base.Dispose();
            _distributedCache.Dispose();
            _messageBus.Dispose();
        }
    }
}

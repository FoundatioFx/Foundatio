using Foundatio.Caching;
using Foundatio.Redis.Messaging;
using StackExchange.Redis;

namespace Foundatio.Redis.Cache {
    public class RedisHybridCacheClient : HybridCacheClient {
        public RedisHybridCacheClient(ConnectionMultiplexer connectionMultiplexer)
            : base(new RedisCacheClient(connectionMultiplexer), new RedisMessageBus(connectionMultiplexer.GetSubscriber())) {}
    }
}

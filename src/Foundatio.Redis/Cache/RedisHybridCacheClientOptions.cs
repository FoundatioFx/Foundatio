using StackExchange.Redis;

namespace Foundatio.Caching {
    public class RedisHybridCacheClientOptions : RedisCacheClientOptions {
        public string RedisChannelName { get; set; } = "cache-messages";
    }

    public class RedisHybridCacheClientOptionsBuilder :
        SharedOptionsBuilder<RedisHybridCacheClientOptions, RedisHybridCacheClientOptionsBuilder> {

        public RedisHybridCacheClientOptionsBuilder ConnectionMultiplexer(IConnectionMultiplexer connectionMultiplexer) {
            Target.ConnectionMultiplexer = connectionMultiplexer;
            return this;
        }

        public RedisHybridCacheClientOptionsBuilder RedisChannelName(string redisChannelName) {
            Target.RedisChannelName = redisChannelName;
            return this;
        }

        public RedisHybridCacheClientOptionsBuilder ShouldThrowOnSerializationError(bool shouldThrow) {
            Target.ShouldThrowOnSerializationError = shouldThrow;
            return this;
        }
    }
}

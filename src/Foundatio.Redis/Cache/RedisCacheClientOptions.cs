using System;
using Foundatio.Serializer;
using StackExchange.Redis;

namespace Foundatio.Caching {
    public class RedisCacheClientOptions : SharedOptions {
        public IConnectionMultiplexer ConnectionMultiplexer { get; set; }
    }

    public class RedisCacheClientOptionsBuilder : SharedOptionsBuilder<RedisCacheClientOptions, RedisCacheClientOptionsBuilder> {
        public RedisCacheClientOptionsBuilder ConnectionMultiplexer(IConnectionMultiplexer connectionMultiplexer) {
            Target.ConnectionMultiplexer = connectionMultiplexer;
            return this;
        }
    }
}
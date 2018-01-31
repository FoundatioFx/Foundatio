using System;
using Foundatio.Serializer;
using StackExchange.Redis;

namespace Foundatio.Caching {
    public class RedisCacheClientOptions : SharedOptions {
        public ConnectionMultiplexer ConnectionMultiplexer { get; set; }
    }

    public class RedisCacheClientOptionsBuilder : SharedOptionsBuilder<RedisCacheClientOptions, RedisCacheClientOptionsBuilder> {
        public RedisCacheClientOptionsBuilder ConnectionMultiplexer(ConnectionMultiplexer connectionMultiplexer) {
            Target.ConnectionMultiplexer = connectionMultiplexer;
            return this;
        }
    }
}
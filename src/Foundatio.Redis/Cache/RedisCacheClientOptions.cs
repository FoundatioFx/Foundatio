using System;
using Foundatio.Serializer;
using StackExchange.Redis;

namespace Foundatio.Caching {
    public class RedisCacheClientOptions : CacheClientOptionsBase {
        public ConnectionMultiplexer ConnectionMultiplexer { get; set; }
        public ISerializer Serializer { get; set; }
    }
}
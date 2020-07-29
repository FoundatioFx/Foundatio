using System;
using Foundatio.Serializer;
using StackExchange.Redis;

namespace Foundatio.Caching {
    public class RedisCacheClientOptions : SharedOptions {
        public IConnectionMultiplexer ConnectionMultiplexer { get; set; }

        /// <summary>
        /// Whether or not an error when deserializing a cache value should result in an exception being thrown or if it should just return an empty cache value
        /// </summary>
        public bool ShouldThrowOnSerializationError { get; set; } = true;

    }

    public class RedisCacheClientOptionsBuilder : SharedOptionsBuilder<RedisCacheClientOptions, RedisCacheClientOptionsBuilder> {
        public RedisCacheClientOptionsBuilder ConnectionMultiplexer(IConnectionMultiplexer connectionMultiplexer) {
            Target.ConnectionMultiplexer = connectionMultiplexer;
            return this;
        }

        public RedisCacheClientOptionsBuilder ShouldThrowOnSerializationError(bool shouldThrow) {
            Target.ShouldThrowOnSerializationError = shouldThrow;
            return this;
        }
    }
}
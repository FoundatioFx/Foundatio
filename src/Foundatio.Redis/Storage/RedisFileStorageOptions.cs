using System;
using StackExchange.Redis;

namespace Foundatio.Storage {
    public class RedisFileStorageOptions : SharedOptions {
        public IConnectionMultiplexer ConnectionMultiplexer { get; set; }
        public string ContainerName { get; set; } = "storage";
    }

    public class RedisFileStorageOptionsBuilder : SharedOptionsBuilder<RedisFileStorageOptions, RedisFileStorageOptionsBuilder> {
        public RedisFileStorageOptionsBuilder ConnectionMultiplexer(IConnectionMultiplexer connectionMultiplexer) {
            Target.ConnectionMultiplexer = connectionMultiplexer;
            return this;
        }

        public RedisFileStorageOptionsBuilder ContainerName(string containerName) {
            Target.ContainerName = containerName ?? throw new ArgumentNullException(nameof(containerName));
            return this;
        }
    }
}

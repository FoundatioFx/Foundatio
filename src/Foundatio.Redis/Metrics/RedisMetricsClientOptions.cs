using System;
using StackExchange.Redis;

namespace Foundatio.Metrics {
    public class RedisMetricsClientOptions : SharedMetricsClientOptions {
        public ConnectionMultiplexer ConnectionMultiplexer { get; set; }
    }

    public class RedisMetricsClientOptionsBuilder : SharedMetricsClientOptionsBuilder<RedisMetricsClientOptions, RedisMetricsClientOptionsBuilder> {
        public RedisMetricsClientOptionsBuilder ConnectionMultiplexer(ConnectionMultiplexer connectionMultiplexer) {
            Target.ConnectionMultiplexer = connectionMultiplexer;
            return this;
        }
    }
}
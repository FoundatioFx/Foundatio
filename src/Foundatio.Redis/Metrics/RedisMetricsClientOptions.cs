using System;
using StackExchange.Redis;

namespace Foundatio.Metrics {
    public class RedisMetricsClientOptions : MetricsClientOptionsBase {
        public ConnectionMultiplexer ConnectionMultiplexer { get; set; }
    }
}
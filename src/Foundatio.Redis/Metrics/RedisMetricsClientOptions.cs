using StackExchange.Redis;

namespace Foundatio.Metrics;

public class RedisMetricsClientOptions : SharedMetricsClientOptions
{
    public IConnectionMultiplexer ConnectionMultiplexer { get; set; }
}

public class RedisMetricsClientOptionsBuilder : SharedMetricsClientOptionsBuilder<RedisMetricsClientOptions, RedisMetricsClientOptionsBuilder>
{
    public RedisMetricsClientOptionsBuilder ConnectionMultiplexer(IConnectionMultiplexer connectionMultiplexer)
    {
        Target.ConnectionMultiplexer = connectionMultiplexer;
        return this;
    }
}

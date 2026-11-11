using System;
using StackExchange.Redis;

namespace Foundatio.Caching;

public class RedisHybridCacheClientOptions : RedisCacheClientOptions
{
    public string RedisChannelName { get; set; } = "cache-messages";
}

public class RedisHybridCacheClientOptionsBuilder :
    SharedOptionsBuilder<RedisHybridCacheClientOptions, RedisHybridCacheClientOptionsBuilder>
{
    public RedisHybridCacheClientOptionsBuilder ConnectionMultiplexer(IConnectionMultiplexer connectionMultiplexer)
    {
        Target.ConnectionMultiplexer = connectionMultiplexer;
        return this;
    }

    public RedisHybridCacheClientOptionsBuilder RedisChannelName(string redisChannelName)
    {
        ArgumentException.ThrowIfNullOrEmpty(redisChannelName);
        Target.RedisChannelName = redisChannelName;
        return this;
    }

    public RedisHybridCacheClientOptionsBuilder ShouldThrowOnSerializationError(bool shouldThrow)
    {
        Target.ShouldThrowOnSerializationError = shouldThrow;
        return this;
    }

    public RedisHybridCacheClientOptionsBuilder UseDatabase(int database)
    {
        if (database < -1)  // We consider -1 as a valid value in respect for the default behaviour of stack exchange redis
        {
            throw new ArgumentOutOfRangeException(nameof(database), "database number cannot be less than 0.");
        }

        Target.Database = database;
        return this;
    }
}

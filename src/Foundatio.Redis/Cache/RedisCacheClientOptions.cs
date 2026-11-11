using System;
using Foundatio.Redis.Utility;
using StackExchange.Redis;

namespace Foundatio.Caching;

public class RedisCacheClientOptions : SharedOptions
{
    public IConnectionMultiplexer? ConnectionMultiplexer { get; set; }

    /// <summary>
    /// Whether or not an error when deserializing a cache value should result in an exception being thrown or if it should just return an empty cache value
    /// </summary>
    public bool ShouldThrowOnSerializationError { get; set; } = true;

    /// <summary>
    /// Controls how read operations are routed in a master-replica topology.
    /// Set to <see cref="CommandFlags.PreferReplica"/> to distribute reads to replica nodes (recommended for master-replica setups).
    /// Writes always go to the master regardless of this setting.
    /// Default is <see cref="CommandFlags.None"/> (reads go to master).
    /// </summary>
    public CommandFlags ReadMode { get; set; } = CommandFlags.None;

    /// <summary>
    /// The database to use
    /// </summary>
    public int Database { get; set; } = -1;
}

public class RedisCacheClientOptionsBuilder : SharedOptionsBuilder<RedisCacheClientOptions, RedisCacheClientOptionsBuilder>
{
    public RedisCacheClientOptionsBuilder ConnectionMultiplexer(IConnectionMultiplexer connectionMultiplexer)
    {
        Target.ConnectionMultiplexer = connectionMultiplexer;
        return this;
    }

    public RedisCacheClientOptionsBuilder ShouldThrowOnSerializationError(bool shouldThrow)
    {
        Target.ShouldThrowOnSerializationError = shouldThrow;
        return this;
    }

    public RedisCacheClientOptionsBuilder ReadMode(CommandFlags commandFlags)
    {
        RedisOptionsValidation.ValidateReadMode(commandFlags);
        Target.ReadMode = commandFlags;
        return this;
    }

    public RedisCacheClientOptionsBuilder UseDatabase(int database)
    {
        if (database < -1)  // We consider -1 as a valid value in respect for the default behaviour of stack exchange redis
        {
            throw new ArgumentOutOfRangeException(nameof(database), "database number cannot be less than -1.");
        }

        Target.Database = database;
        return this;
    }
}

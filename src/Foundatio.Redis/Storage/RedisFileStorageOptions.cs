using System;
using Foundatio.Redis.Utility;
using StackExchange.Redis;

namespace Foundatio.Storage;

public class RedisFileStorageOptions : SharedOptions
{
    public IConnectionMultiplexer ConnectionMultiplexer { get; set; }
    public string ContainerName { get; set; } = "storage";

    /// <summary>
    /// Controls how read operations are routed in a master-replica topology.
    /// Set to <see cref="CommandFlags.PreferReplica"/> to distribute reads to replica nodes.
    /// Writes always go to the master regardless of this setting.
    /// Default is <see cref="CommandFlags.None"/> (reads go to master).
    /// </summary>
    public CommandFlags ReadMode { get; set; } = CommandFlags.None;
}

public class RedisFileStorageOptionsBuilder : SharedOptionsBuilder<RedisFileStorageOptions, RedisFileStorageOptionsBuilder>
{
    public RedisFileStorageOptionsBuilder ConnectionMultiplexer(IConnectionMultiplexer connectionMultiplexer)
    {
        Target.ConnectionMultiplexer = connectionMultiplexer;
        return this;
    }

    public RedisFileStorageOptionsBuilder ContainerName(string containerName)
    {
        Target.ContainerName = containerName ?? throw new ArgumentNullException(nameof(containerName));
        return this;
    }

    public RedisFileStorageOptionsBuilder ReadMode(CommandFlags commandFlags)
    {
        RedisOptionsValidation.ValidateReadMode(commandFlags);
        Target.ReadMode = commandFlags;
        return this;
    }
}

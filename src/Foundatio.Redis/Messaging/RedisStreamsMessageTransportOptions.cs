using System;
using Foundatio.Jobs;
using StackExchange.Redis;

namespace Foundatio.Messaging;

public class RedisStreamsMessageTransportOptions
{
    /// <summary>The Redis connection to use. Required.</summary>
    public IConnectionMultiplexer ConnectionMultiplexer { get; set; } = null!;

    /// <summary>Prefix applied to every stream/key this transport creates. Isolates environments/runs on a shared Redis.</summary>
    public string KeyPrefix { get; set; } = "fnd:msg:";

    /// <summary>Consumer-group name used for plain queue destinations (its members are competing consumers).</summary>
    public string DefaultConsumerGroup { get; set; } = "foundatio";

    /// <summary>How long a received message stays invisible to other consumers before it can be reclaimed (the lease).</summary>
    public TimeSpan DefaultVisibilityTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum retained messages per destination. Sends fail at capacity; unread or pending work is never trimmed.</summary>
    public int MaxPendingMessages { get; set; } = 100_000;
    /// <summary>Maximum concurrently pipelined sends per call.</summary>
    public int MaxBatchSize { get; set; } = 64;
    /// <summary>Budgets for the automatic delayed-message store when no shared job runtime store is configured.</summary>
    public JobRuntimeStoreOptions Scheduling { get; set; } = new();
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(25);
    /// <summary>Idle polling backs off to this ceiling; lower it when arrival latency matters more than idle broker traffic.</summary>
    public TimeSpan MaxIdlePollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>This node's consumer name within every group (defaults to a stable per-instance id). Distinct instances are competing consumers.</summary>
    public string? ConsumerName { get; set; }

    /// <summary>Time source (defaults to <see cref="TimeProvider.System"/>).</summary>
    public TimeProvider? TimeProvider { get; set; }
}

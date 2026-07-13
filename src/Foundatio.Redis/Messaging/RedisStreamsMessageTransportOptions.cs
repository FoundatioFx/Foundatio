using System;
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

    /// <summary>Approximate <c>MAXLEN</c> cap applied on <c>XADD</c> (null = no trimming). Trimming can drop un-acked entries; keep ample headroom.</summary>
    public int? MaxStreamLength { get; set; }

    /// <summary>This node's consumer name within every group (defaults to a stable per-instance id). Distinct instances are competing consumers.</summary>
    public string? ConsumerName { get; set; }

    /// <summary>Time source (defaults to <see cref="TimeProvider.System"/>).</summary>
    public TimeProvider? TimeProvider { get; set; }
}

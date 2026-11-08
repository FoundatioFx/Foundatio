using System;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging.Transport;

public sealed record RedisMessageTransportOptions
{
    /// <summary>Prepended to every key. Each source's keys share a hash tag, so a source lives on one cluster slot.</summary>
    public string KeyPrefix { get; init; } = "foundatio:messaging";

    /// <summary>The Redis database to use, or -1 for the connection's default.</summary>
    public int Database { get; init; } = -1;

    /// <summary>
    /// How long an instance subscription survives without a heartbeat. Its receiver renews it every third of this
    /// time; once it lapses (the process stopped), publishers and new receivers on the topic delete it.
    /// </summary>
    public TimeSpan InstanceSubscriptionTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a <see cref="OutgoingMessage.DeduplicationId"/> is remembered.</summary>
    public TimeSpan DeduplicationWindow { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The first wait between polls of an empty source. Waits double up to <see cref="MaxPollInterval"/>.</summary>
    public TimeSpan MinPollInterval { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>The longest wait between polls of an empty source, which bounds the delivery latency of new messages.</summary>
    public TimeSpan MaxPollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Used to turn <see cref="OutgoingMessage.DeliverAt"/> into a delay. Delays are then measured by the Redis clock.</summary>
    public TimeProvider? TimeProvider { get; init; }

    public ILoggerFactory? LoggerFactory { get; init; }
}

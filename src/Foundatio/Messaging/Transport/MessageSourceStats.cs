namespace Foundatio.Messaging.Transport;

/// <summary>
/// Approximate counts of the messages in one queue or durable subscription, as the broker reports them, for
/// dashboards and alerts. Each count is null when the transport cannot tell.
/// </summary>
/// <remarks>
/// The counts are a snapshot read from the broker, not a transaction: they may lag sends and settlements by a few
/// seconds, and they may not add up exactly while messages move between states. A transport whose broker cannot tell
/// two states apart counts the message in one of them and documents which: a broker that hides a retry until its
/// delay passes may count it as in flight, and one that cannot see leases (<see cref="InFlight"/> is null) counts a
/// delivered message as queued until it is settled.
/// </remarks>
public sealed record MessageSourceStats
{
    /// <summary>The queue or durable subscription that was counted.</summary>
    public required MessageSource Source { get; init; }

    /// <summary>
    /// Messages waiting to be delivered now: not delayed and not leased to a receiver. When <see cref="InFlight"/> is
    /// null, it may also include delivered messages that are not settled yet.
    /// </summary>
    public long? Queued { get; init; }

    /// <summary>Messages waiting for a delivery time in the future, including retries waiting out their delay.</summary>
    public long? Delayed { get; init; }

    /// <summary>Messages delivered to a receiver and not yet completed, abandoned or dead-lettered.</summary>
    public long? InFlight { get; init; }

    /// <summary>Messages in the source's dead-letter queue.</summary>
    public long? DeadLettered { get; init; }
}

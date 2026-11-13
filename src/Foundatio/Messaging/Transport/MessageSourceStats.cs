namespace Foundatio.Messaging.Transport;

/// <summary>
/// Approximate counts of the messages in one queue or durable subscription, as the broker reports them, for
/// dashboards and alerts. Each count is null when the transport cannot tell.
/// </summary>
/// <remarks>
/// The counts are a snapshot read from the broker, not a transaction: they may lag sends and settlements by a few
/// seconds, and they may not add up exactly while messages move between states.
/// </remarks>
public sealed record MessageSourceStats
{
    /// <summary>The queue or durable subscription that was counted.</summary>
    public required MessageSource Source { get; init; }

    /// <summary>Messages waiting to be delivered now: not delayed and not leased to a receiver.</summary>
    public long? Queued { get; init; }

    /// <summary>Messages waiting for a delivery time in the future, including retries waiting out their delay.</summary>
    public long? Delayed { get; init; }

    /// <summary>Messages delivered to a receiver and not yet completed, abandoned or dead-lettered.</summary>
    public long? InFlight { get; init; }

    /// <summary>Messages in the source's dead-letter queue.</summary>
    public long? DeadLettered { get; init; }
}

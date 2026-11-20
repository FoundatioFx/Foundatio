using System;
using System.Collections.Generic;

namespace Foundatio.Messaging;

/// <summary>Options shared by <see cref="SendOptions"/> and <see cref="PublishOptions"/>.</summary>
public abstract record DeliveryOptions
{
    /// <summary>The message id, up to 128 characters. Generated when omitted. Not allowed for batches.</summary>
    public string? MessageId { get; init; }

    /// <summary>Defaults to the correlation id of the message being handled, if any.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Deliver after this delay.</summary>
    public TimeSpan? Delay { get; init; }

    /// <summary>Deliver at this time. Takes precedence over <see cref="Delay"/>.</summary>
    public DateTimeOffset? DeliverAt { get; init; }

    /// <summary>Discard the message if it has not been handled within this time.</summary>
    public TimeSpan? TimeToLive { get; init; }

    /// <summary>
    /// Related messages, such as all events for one order or tenant (up to 128 characters). Transports that support it
    /// deliver messages with the same key in order, one at a time (SQS FIFO message groups, Service Bus sessions,
    /// Kafka partition keys).
    /// </summary>
    public string? GroupKey { get; init; }

    /// <summary>
    /// Up to 128 characters. Transports that support it drop later messages with the same id within their
    /// deduplication window.
    /// </summary>
    public string? DeduplicationId { get; init; }

    /// <summary>Application headers delivered with the message.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}

/// <summary>Options for <see cref="IMessageClient.SendAsync{T}"/>.</summary>
public sealed record SendOptions : DeliveryOptions
{
    /// <summary>Overrides the queue the message type is routed to.</summary>
    public string? Queue { get; init; }
}

/// <summary>Options for <see cref="IMessageClient.PublishAsync{T}"/>.</summary>
public sealed record PublishOptions : DeliveryOptions
{
    /// <summary>Overrides the topic the message type is routed to.</summary>
    public string? Topic { get; init; }
}

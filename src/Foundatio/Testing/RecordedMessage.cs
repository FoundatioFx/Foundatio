using System;
using System.Collections.Generic;

namespace Foundatio.Testing;

/// <summary>A message <see cref="FoundatioTestHarness"/> saw being sent, published, or delivered to a handler.</summary>
public sealed record RecordedMessage
{
    /// <summary>The message's id.</summary>
    public required string MessageId { get; init; }

    /// <summary>The message's wire name, from <c>[Message]</c>, <c>MapMessage</c> or its full type name.</summary>
    public required string MessageType { get; init; }

    /// <summary>The message. Null for a delivery whose type is unknown here or whose body could not be deserialized.</summary>
    public object? Message { get; init; }

    /// <summary>
    /// Where it was sent (<c>queue:name</c> or <c>topic:name</c>), or the source it was delivered from
    /// (<c>queue:name</c>, <c>subscription:topic/name</c> or <c>instance:topic/name</c>).
    /// </summary>
    public required string Address { get; init; }

    /// <summary>The correlation id it was sent with, or null.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>The delivery attempt, starting at 1. Zero for sent and published messages.</summary>
    public int Attempt { get; init; }

    /// <summary>Every header, including <see cref="Foundatio.Messaging.KnownHeaders"/>.</summary>
    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    /// <summary>For sent and published messages, the requested delivery time of a delayed message.</summary>
    public DateTimeOffset? DeliverAt { get; init; }

    /// <summary>How the delivery ended. Null for sent and published messages.</summary>
    public MessageOutcome? Outcome { get; init; }

    /// <summary>The dead-letter reason, or the handler's error, for deliveries that did not complete.</summary>
    public string? Error { get; init; }

    /// <summary>The exception a handler threw, if any.</summary>
    public Exception? Exception { get; init; }
}

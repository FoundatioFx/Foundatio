using System;
using System.Collections.Generic;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// A leased delivery returned by <see cref="IMessageReceiver.ReceiveAsync"/>. Transports derive from this type to
/// carry their receipt (delivery tag, receipt handle, stream entry id, offset).
/// </summary>
public class IncomingMessage
{
    public required string MessageId { get; init; }
    public required ReadOnlyMemory<byte> Body { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } = EmptyHeaders.Instance;

    /// <summary>How many times this message has been delivered, starting at 1.</summary>
    public int DeliveryCount { get; init; } = 1;

    /// <summary>When the message was sent, if the broker records it.</summary>
    public DateTimeOffset? EnqueuedAt { get; init; }
    public string? GroupKey { get; init; }
}

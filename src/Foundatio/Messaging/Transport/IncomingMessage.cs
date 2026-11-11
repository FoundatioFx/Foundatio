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

    /// <summary>
    /// How many times this message has been delivered, starting at 1. Foundatio uses it as the attempt number, so
    /// deliveries released by a disposed receiver may count, as they do on brokers that count every receive. Brokers
    /// that track progress by offset (Kafka) may restart the count after a crash.
    /// </summary>
    public int DeliveryCount { get; init; } = 1;

    /// <summary>When the message was sent, if the broker records it.</summary>
    public DateTimeOffset? EnqueuedAt { get; init; }
    /// <summary>The <see cref="OutgoingMessage.GroupKey"/> it was sent with. Must round-trip even when groups are not ordered.</summary>
    public string? GroupKey { get; init; }
}

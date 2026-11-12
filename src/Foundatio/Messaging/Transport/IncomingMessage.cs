using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// A leased delivery returned by <see cref="IMessageReceiver.ReceiveAsync"/>. Transports derive from this type to
/// carry their receipt (delivery tag, receipt handle, stream entry id, offset).
/// </summary>
[Experimental(ProviderApi.DiagnosticId, UrlFormat = ProviderApi.UrlFormat)]
public class IncomingMessage
{
    /// <summary>The <see cref="OutgoingMessage.MessageId"/> it was sent with.</summary>
    public required string MessageId { get; init; }
    /// <summary>The serialized message, exactly as it was sent.</summary>
    public required ReadOnlyMemory<byte> Body { get; init; }
    /// <summary>The headers it was sent with, unchanged.</summary>
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

    /// <summary>
    /// When the broker will end this delivery's lease, for a transport whose broker decides that itself rather than
    /// honouring <see cref="ReceiverOptions.LeaseDuration"/>: for example a Service Bus lock, whose duration is set per
    /// entity. Leave it null when the lease follows <see cref="ReceiverOptions.LeaseDuration"/>.
    /// </summary>
    /// <remarks>
    /// Only the transport writes it: in <see cref="IMessageReceiver.ReceiveAsync"/>, and again in
    /// <see cref="IMessageReceiver.RenewAsync"/> when the broker extends the lease by its own duration. Foundatio reads it
    /// after those calls return, and then renews a third of the way through what is left of the lease, as it does for
    /// leases it requests, and bounds renewal retries by it.
    /// </remarks>
    public DateTimeOffset? LeaseExpiresAt { get; set; }
}

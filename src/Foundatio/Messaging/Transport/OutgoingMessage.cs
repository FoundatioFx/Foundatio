using System;
using System.Collections.Generic;

namespace Foundatio.Messaging.Transport;

/// <summary>A message handed to <see cref="IMessageTransport.SendAsync"/>.</summary>
public sealed record OutgoingMessage
{
    /// <summary>
    /// The longest <see cref="MessageId"/>, <see cref="GroupKey"/> or <see cref="DeduplicationId"/> Foundatio sends,
    /// so transports can map them to broker limits (SQS FIFO allows 128 characters).
    /// </summary>
    public const int MaxIdLength = 128;

    public required string MessageId { get; init; }
    public required ReadOnlyMemory<byte> Body { get; init; }

    /// <summary>Headers that must be delivered unchanged.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = EmptyHeaders.Instance;

    /// <summary>
    /// Do not deliver before this time. Never further away than <see cref="TransportCapabilities.MaxDelay"/> (or
    /// <see cref="TransportCapabilities.MaxPublishDelay"/> for a topic). It comes
    /// from the sender's clock; transports may convert it to a delay relative to their own clock when sending.
    /// </summary>
    public DateTimeOffset? DeliverAt { get; init; }

    /// <summary>Messages with the same group key are ordered when <see cref="TransportCapabilities.OrderedGroups"/> is true.</summary>
    public string? GroupKey { get; init; }

    /// <summary>
    /// When <see cref="TransportCapabilities.Deduplication"/> is true, a later message with the same id sent to the
    /// same destination within the transport's deduplication window is dropped.
    /// </summary>
    public string? DeduplicationId { get; init; }
}

internal static class EmptyHeaders
{
    public static readonly IReadOnlyDictionary<string, string> Instance = new Dictionary<string, string>();
}

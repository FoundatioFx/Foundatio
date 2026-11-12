using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Foundatio.Messaging.Transport;

/// <summary>A message handed to <see cref="IMessageTransport.SendAsync"/>.</summary>
[Experimental(ProviderApi.DiagnosticId, UrlFormat = ProviderApi.UrlFormat)]
public sealed record OutgoingMessage
{
    /// <summary>
    /// The longest <see cref="MessageId"/>, <see cref="GroupKey"/> or <see cref="DeduplicationId"/> Foundatio sends, in
    /// UTF-16 characters. They may contain any characters, including spaces and non-ASCII ones; transports map them to
    /// what the broker accepts (SQS FIFO allows 128 printable ASCII characters), for example by hashing.
    /// </summary>
    public const int MaxIdLength = 128;

    /// <summary>Identifies the message; at most <see cref="MaxIdLength"/> characters. Delivered unchanged as <see cref="IncomingMessage.MessageId"/>.</summary>
    public required string MessageId { get; init; }
    /// <summary>The serialized message. May be empty.</summary>
    public required ReadOnlyMemory<byte> Body { get; init; }

    /// <summary>
    /// Headers that must be delivered unchanged. Names starting with <c>x-foundatio-</c> are reserved for transports to
    /// use on the wire: a transport may reject an application header with such a name with
    /// <see cref="ArgumentException"/>, or deliver it unchanged.
    /// </summary>
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
    /// same destination within the transport's deduplication window is dropped, whether it comes in the same batch or
    /// from another process. The same id sent to another destination is not a duplicate.
    /// </summary>
    public string? DeduplicationId { get; init; }
}

internal static class EmptyHeaders
{
    public static readonly IReadOnlyDictionary<string, string> Instance = new Dictionary<string, string>();
}

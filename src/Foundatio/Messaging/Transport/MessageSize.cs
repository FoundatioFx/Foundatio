using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// How Foundatio and the conformance suite measure a message against <see cref="TransportCapabilities.MaxMessageSize"/>.
/// It counts no overhead per header; transports reserve room for their own framing when they declare the limit.
/// </summary>
[Experimental(ProviderApi.DiagnosticId, UrlFormat = ProviderApi.UrlFormat)]
public static class MessageSize
{
    /// <summary>Body bytes plus the UTF-8 bytes of every header name and value, the message id, the group key and the deduplication id.</summary>
    public static long Of(OutgoingMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Of(message.MessageId, message.Body.Span, message.Headers, message.GroupKey, message.DeduplicationId);
    }

    /// <inheritdoc cref="Of(OutgoingMessage)"/>
    public static long Of(string messageId, ReadOnlySpan<byte> body, IReadOnlyDictionary<string, string> headers, string? groupKey = null, string? deduplicationId = null)
    {
        ArgumentNullException.ThrowIfNull(messageId);
        ArgumentNullException.ThrowIfNull(headers);

        long size = body.Length + Encoding.UTF8.GetByteCount(messageId);
        foreach (var header in headers)
            size += Encoding.UTF8.GetByteCount(header.Key) + Encoding.UTF8.GetByteCount(header.Value);
        if (groupKey is not null)
            size += Encoding.UTF8.GetByteCount(groupKey);
        if (deduplicationId is not null)
            size += Encoding.UTF8.GetByteCount(deduplicationId);

        return size;
    }
}

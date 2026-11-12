namespace Foundatio.Messaging;

/// <summary>Header names Foundatio writes. Application headers should not reuse them.</summary>
public static class KnownHeaders
{
    /// <summary>The stable message type name used to pick the handler.</summary>
    public const string MessageType = "message-type";

    /// <summary>
    /// The names of the message type's base classes and interfaces, most derived first, separated by commas. A
    /// subscriber that does not know <see cref="MessageType"/> uses them to find handlers for a base type.
    /// </summary>
    public const string MessageBaseTypes = "message-base-types";

    public const string CorrelationId = "correlation-id";

    /// <summary>When the message was first sent, in round-trip ("O") format.</summary>
    public const string SentAt = "sent-at";

    /// <summary>W3C trace context, propagated so a handler's activity continues the sender's trace.</summary>
    public const string TraceParent = "traceparent";

    public const string TraceState = "tracestate";

    /// <summary>The requested delivery time when it is beyond the transport's native delay limit.</summary>
    public const string DeliverAt = "deliver-at";

    /// <summary>After this time the message is discarded instead of handled.</summary>
    public const string ExpiresAt = "expires-at";

    /// <summary>Why a message was dead-lettered, at most 1,024 UTF-8 bytes. Added by the transport.</summary>
    public const string DeadLetterReason = "dead-letter-reason";
}

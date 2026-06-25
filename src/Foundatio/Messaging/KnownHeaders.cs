namespace Foundatio.Messaging;

public static class KnownHeaders
{
    public const string MessageType = "message.type";
    public const string ContentType = "message.content_type";
    public const string CorrelationId = "message.correlation_id";
    public const string TraceParent = "traceparent";
    public const string TraceState = "tracestate";
    public const string Priority = "message.priority";
    public const string Expiration = "message.expiration";
    public const string Attempts = "message.attempts";
    public const string DeadLetterReason = "message.dead_letter.reason";
}

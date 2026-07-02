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

    // Forensics stamped by the core when a message is dead-lettered, so a dead message is triageable with plain
    // transport tooling. These names are a compatibility contract; values are truncated to fit transport limits.
    public const string DeadLetterExceptionType = "message.dead_letter.exception_type";
    public const string DeadLetterExceptionMessage = "message.dead_letter.exception_message";
    public const string DeadLetterExceptionStackTrace = "message.dead_letter.exception_stack";
    public const string DeadLetterFailedAt = "message.dead_letter.failed_at";
    public const string DeadLetterOriginalDestination = "message.dead_letter.original_destination";
}

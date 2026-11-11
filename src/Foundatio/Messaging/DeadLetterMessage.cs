using System;
using System.Collections.Generic;

namespace Foundatio.Messaging;

/// <summary>A message in a dead-letter queue, returned by <see cref="IMessageAdmin.PeekDeadLettersAsync"/>.</summary>
public sealed record DeadLetterMessage
{
    public required string MessageId { get; init; }

    /// <summary>The message's wire name, or empty when it had none.</summary>
    public required string MessageType { get; init; }

    /// <summary>Why it was dead-lettered: the last exception, or the reason passed to <see cref="IMessageContext.DeadLetter"/>.</summary>
    public string? Reason { get; init; }

    public string? CorrelationId { get; init; }
    public string? GroupKey { get; init; }
    public DateTimeOffset? SentAt { get; init; }

    /// <summary>When it entered the dead-letter queue, if the broker records it.</summary>
    public DateTimeOffset? DeadLetteredAt { get; init; }

    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public ReadOnlyMemory<byte> Body { get; init; }

    /// <summary>The deserialized message when its type is registered in this app and its body is readable; otherwise null.</summary>
    public object? Message { get; init; }
}

using System;
using System.Collections.Generic;

namespace Foundatio.Messaging;

/// <summary>A message in a dead-letter queue, returned by <see cref="IMessageAdmin.PeekDeadLettersAsync"/>.</summary>
public sealed record DeadLetterMessage
{
    /// <summary>The id the message was sent with.</summary>
    public required string MessageId { get; init; }

    /// <summary>The message's wire name, or empty when it had none.</summary>
    public required string MessageType { get; init; }

    /// <summary>Why it was dead-lettered: the last exception, or the reason passed to <see cref="IMessageContext.DeadLetter"/>.</summary>
    public string? Reason { get; init; }

    /// <summary>The correlation id it was sent with, or null.</summary>
    public string? CorrelationId { get; init; }
    /// <summary>The group key it was sent with, or null.</summary>
    public string? GroupKey { get; init; }
    /// <summary>When it was first sent, or null when that is unknown.</summary>
    public DateTimeOffset? SentAt { get; init; }

    /// <summary>When it entered the dead-letter queue, if the broker records it.</summary>
    public DateTimeOffset? DeadLetteredAt { get; init; }

    /// <summary>Every header it was sent with, including <see cref="KnownHeaders"/> and the dead-letter reason.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    /// <summary>The serialized message, as it was sent.</summary>
    public ReadOnlyMemory<byte> Body { get; init; }

    /// <summary>The deserialized message when its type is registered in this app and its body is readable; otherwise null.</summary>
    public object? Message { get; init; }
}

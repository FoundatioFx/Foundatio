using System;
using System.Collections.Generic;
using System.Threading;

namespace Foundatio.Messaging;

internal sealed class MessageContext : IMessageContext
{
    private static readonly AsyncLocal<MessageContext?> CurrentContext = new();

    public required string MessageId { get; init; }
    public required string MessageType { get; init; }
    public string? CorrelationId { get; init; }
    public int Attempt { get; init; }
    public DateTimeOffset? SentAt { get; init; }
    public string? GroupKey { get; init; }
    public required IReadOnlyDictionary<string, string> Headers { get; init; }
    public required string Source { get; init; }
    public IServiceProvider Services { get; set; } = Testing.EmptyServiceProvider.Instance;

    public string? DeadLetterReason { get; private set; }
    public bool RetryRequested { get; private set; }
    public TimeSpan? RetryDelay { get; private set; }

    /// <summary>The message being handled on this async flow, used to propagate the correlation id.</summary>
    public static MessageContext? Current
    {
        get => CurrentContext.Value;
        set => CurrentContext.Value = value;
    }

    public void DeadLetter(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        DeadLetterReason = reason;
    }

    public void Retry(TimeSpan? delay = null)
    {
        if (delay is { } value)
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero, nameof(delay));

        RetryRequested = true;
        RetryDelay = delay;
    }
}

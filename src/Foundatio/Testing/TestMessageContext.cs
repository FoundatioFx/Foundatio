using System;
using System.Collections.Generic;
using Foundatio.Messaging;

namespace Foundatio.Testing;

/// <summary>An <see cref="IMessageContext"/> for unit testing a handler. Records the outcome the handler chose.</summary>
/// <example>
/// <code>
/// var context = new TestMessageContext();
/// await new OrderPlacedHandler(...).HandleAsync(new OrderPlaced(1), context, CancellationToken.None);
/// Assert.Null(context.DeadLetterReason);
/// </code>
/// </example>
public sealed class TestMessageContext : IMessageContext
{
    public string MessageId { get; init; } = Guid.NewGuid().ToString("N");
    public string MessageType { get; init; } = "test";
    public string? CorrelationId { get; init; }
    public int Attempt { get; init; } = 1;
    public DateTimeOffset? SentAt { get; init; }
    public string? GroupKey { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public string Source { get; init; } = "queue:test";
    public IServiceProvider Services { get; init; } = EmptyServiceProvider.Instance;

    /// <summary>Set when the handler called <see cref="DeadLetter"/>.</summary>
    public string? DeadLetterReason { get; private set; }

    /// <summary>True when the handler called <see cref="Retry"/>.</summary>
    public bool RetryRequested { get; private set; }

    public TimeSpan? RetryDelay { get; private set; }

    public void DeadLetter(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        DeadLetterReason = reason;
    }

    public void Retry(TimeSpan? delay = null)
    {
        RetryRequested = true;
        RetryDelay = delay;
    }
}

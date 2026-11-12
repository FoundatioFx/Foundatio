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
    /// <inheritdoc />
    public string MessageId { get; init; } = Guid.NewGuid().ToString("N");
    /// <inheritdoc />
    public string MessageType { get; init; } = "test";
    /// <inheritdoc />
    public string? CorrelationId { get; init; }
    /// <inheritdoc />
    public int Attempt { get; init; } = 1;
    /// <inheritdoc />
    public DateTimeOffset? SentAt { get; init; }
    /// <inheritdoc />
    public string? GroupKey { get; init; }
    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    /// <inheritdoc />
    public string Source { get; init; } = "queue:test";
    /// <inheritdoc />
    public IServiceProvider Services { get; init; } = EmptyServiceProvider.Instance;

    /// <summary>Set when the handler called <see cref="DeadLetter"/>.</summary>
    public string? DeadLetterReason { get; private set; }

    /// <summary>True when the handler called <see cref="Retry"/>.</summary>
    public bool RetryRequested { get; private set; }

    /// <summary>The delay the handler passed to <see cref="Retry"/>, or null.</summary>
    public TimeSpan? RetryDelay { get; private set; }

    /// <summary>Records <paramref name="reason"/> in <see cref="DeadLetterReason"/>.</summary>
    public void DeadLetter(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        DeadLetterReason = reason;
    }

    /// <summary>Sets <see cref="RetryRequested"/> and records <paramref name="delay"/> in <see cref="RetryDelay"/>.</summary>
    public void Retry(TimeSpan? delay = null)
    {
        RetryRequested = true;
        RetryDelay = delay;
    }
}

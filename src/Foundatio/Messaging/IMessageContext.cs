using System;
using System.Collections.Generic;

namespace Foundatio.Messaging;

/// <summary>Information about the message being handled, and ways to decide its outcome without throwing.</summary>
public interface IMessageContext
{
    string MessageId { get; }

    /// <summary>The stable type name the message was sent with.</summary>
    string MessageType { get; }

    string? CorrelationId { get; }

    /// <summary>The 1-based delivery attempt.</summary>
    int Attempt { get; }

    DateTimeOffset? SentAt { get; }

    string? GroupKey { get; }

    /// <summary>All headers, including <see cref="KnownHeaders"/>.</summary>
    IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>Where the message was received from, for example <c>queue:orders</c>.</summary>
    string Source { get; }

    /// <summary>The DI scope created for this handler invocation. Handler classes should prefer constructor injection.</summary>
    IServiceProvider Services { get; }

    /// <summary>When the handler returns, move the message to the dead-letter queue instead of completing it.</summary>
    void DeadLetter(string reason);

    /// <summary>When the handler returns, deliver the message again after <paramref name="delay"/> (or the retry policy's delay).</summary>
    /// <remarks>This counts as an attempt; once attempts run out the message is dead-lettered.</remarks>
    void Retry(TimeSpan? delay = null);
}

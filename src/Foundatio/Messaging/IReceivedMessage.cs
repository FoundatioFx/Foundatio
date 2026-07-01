using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Messaging;

public enum AckMode
{
    Auto,
    Manual
}

/// <summary>
/// Core-owned retry and dead-letter policy. Foundatio always owns redelivery and dead-lettering so the behavior is
/// identical across transports; transports stay simple and only provide the underlying primitives (redelivery and an
/// optional dead-letter sink). Configure a default on <see cref="MessageBusOptions"/>; a subscription can override
/// <see cref="MaxAttempts"/>/backoff per subscription.
/// </summary>
public sealed record RetryPolicy
{
    /// <summary>Maximum delivery attempts for a failing handler before the message is dead-lettered. Default 5.</summary>
    public int MaxAttempts { get; init; } = 5;

    /// <summary>Delay before each redelivery given the 1-based attempt number. Null defers to the transport's own redelivery timing.</summary>
    public Func<int, TimeSpan>? Backoff { get; init; }

    /// <summary>
    /// Destination terminal (dead-lettered) messages are sent to when the transport has no native dead-letter sink.
    /// Null drops terminal messages on such transports. Ignored when the transport supports native dead-lettering.
    /// </summary>
    public string? DeadLetterDestination { get; init; }

    /// <summary>Maximum attempts for a message whose type has no registered consumer before it is dead-lettered as "no-handler". Default 50.</summary>
    public int UnmatchedMaxAttempts { get; init; } = 50;

    /// <summary>Delay before redelivering an unmatched-type message. Null defers to the transport's own redelivery timing.</summary>
    public Func<int, TimeSpan>? UnmatchedBackoff { get; init; }
}

/// <summary>
/// Thrown by the consumer loop when a message arrives on a shared destination whose type has no registered consumer
/// on this node (for example a newer message type mid rolling-deploy, or a misconfiguration). It is surfaced loudly
/// per message and isolated to that message — the receive loop and the other type handlers keep running.
/// </summary>
public sealed class UnhandledMessageTypeException : Exception
{
    public UnhandledMessageTypeException(string? messageType, string source)
        : base($"No consumer is registered for message type \"{messageType ?? "(unknown)"}\" received on source \"{source}\".")
    {
        MessageType = messageType;
        SourceName = source;
    }

    public string? MessageType { get; }
    public string SourceName { get; }
}

public sealed record RejectOptions
{
    /// <summary>
    /// When false (default) the message is returned for redelivery (a retry). When true the message is terminal: it
    /// is moved to the transport's dead-letter sink where one exists, otherwise dropped. Terminal messages are never
    /// redelivered.
    /// </summary>
    public bool Terminal { get; init; }

    /// <summary>Reason carried to the dead-letter sink (where the transport supports one) for a terminal reject.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// An explicit delay before the message is redelivered. Honored only for a non-terminal reject, served natively
    /// when the transport supports redelivery delay within its advertised maximum, otherwise through the runtime store.
    /// When null the transport's own redelivery timing applies.
    /// </summary>
    public TimeSpan? RedeliveryDelay { get; init; }
}

public interface IReceivedMessage
{
    string Id { get; }
    ReadOnlyMemory<byte> Body { get; }
    MessageHeaders Headers { get; }
    string? CorrelationId { get; }
    string? MessageType { get; }
    MessagePriority Priority { get; }
    int Attempts { get; }
    bool IsHandled { get; }
    CancellationToken CancellationToken { get; }
    Task CompleteAsync(CancellationToken cancellationToken = default);
    Task RejectAsync(RejectOptions? options = null, CancellationToken cancellationToken = default);
    Task RenewLockAsync(TimeSpan? duration = null, CancellationToken cancellationToken = default);
}

public interface IReceivedMessage<out T> : IReceivedMessage where T : class
{
    T Message { get; }
}

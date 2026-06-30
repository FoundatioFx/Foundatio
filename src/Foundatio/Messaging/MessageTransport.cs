using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Messaging;

public enum MessagePriority
{
    Low = 0,
    Normal = 1,
    High = 2
}

public enum DeliveryGuarantee
{
    AtMostOnce,
    AtLeastOnce
}

public enum OrderingGuarantee
{
    None,
    Fifo,
    PerPartition
}

public enum DestinationRole
{
    Queue,
    Topic,
    Subscription,
    Binding
}

public sealed record TransportMessage
{
    public required ReadOnlyMemory<byte> Body { get; init; }
    public MessageHeaders Headers { get; init; } = MessageHeaders.Empty;
    public string? MessageId { get; init; }

    /// <summary>
    /// Content type of <see cref="Body"/> (e.g. <c>application/json</c>). A transport whose native wire format is text
    /// (such as SQS/SNS) can store a text body directly when this indicates text, avoiding base64 overhead; null means
    /// unknown, so a byte-safe encoding should be used.
    /// </summary>
    public string? ContentType { get; init; }
}

public sealed record TransportSendOptions
{
    public MessagePriority Priority { get; init; } = MessagePriority.Normal;
    public DateTimeOffset? DeliverAt { get; init; }
    public string? DeduplicationId { get; init; }
    public string? PartitionKey { get; init; }

    /// <summary>
    /// The role of the destination being sent to. Lets a transport route the send without inferring (for example, a
    /// queue send to SQS vs. a topic publish to SNS) — the caller always knows whether it is sending to a queue or a
    /// topic, so it states it rather than relying on prior provisioning.
    /// </summary>
    public DestinationRole DestinationRole { get; init; } = DestinationRole.Queue;
}

public sealed record TransportEntry
{
    public required string Id { get; init; }
    public required string Destination { get; init; }
    public required ReadOnlyMemory<byte> Body { get; init; }
    public MessageHeaders Headers { get; init; } = MessageHeaders.Empty;
    public int DeliveryCount { get; init; } = 1;
    public DateTimeOffset? EnqueuedUtc { get; init; }
    public required Receipt Receipt { get; init; }
}

public readonly struct Receipt
{
    public object? TransportState { get; init; }
}

public sealed record ReceiveRequest
{
    public int MaxMessages { get; init; } = 1;
    public TimeSpan? MaxWaitTime { get; init; }
}

public sealed record MessageDestinationStats
{
    // Point-in-time gauges every transport can report (may be approximate / eventually consistent on real brokers,
    // e.g. SQS ApproximateNumberOf*).
    public long Queued { get; init; }
    public long Working { get; init; }
    public long Deadletter { get; init; }

    // Lifetime counters. Not universally available — a transport that does not track a counter leaves it null (e.g.
    // SQS exposes no lifetime "completed" count). Null means "not reported", distinct from a reported zero.
    public long? Enqueued { get; init; }
    public long? Dequeued { get; init; }
    public long? Completed { get; init; }
    public long? Abandoned { get; init; }
    public long? Errors { get; init; }
    public long? Timeouts { get; init; }
}

public sealed record SendItemResult
{
    /// <summary>The broker-assigned id of the accepted message.</summary>
    public string? MessageId { get; init; }
}

/// <summary>
/// The result of a successful <see cref="IMessageTransport.SendAsync"/>: the accepted messages' ids, in order.
/// </summary>
/// <remarks>
/// Send is throw-on-failure: a transport throws for any failure rather than returning a failed item, so every item in
/// <see cref="Items"/> was accepted. A multi-message send is NOT atomic — if a later message fails, earlier messages
/// may already have been delivered before the exception propagates.
/// </remarks>
public sealed record SendResult
{
    public required IReadOnlyList<SendItemResult> Items { get; init; }
}

/// <summary>
/// Thrown when a transport settle operation is given a receipt that has expired or was already settled. Strict receipt
/// validation is transport-specific: some brokers (e.g. SQS) treat settling with a stale receipt as idempotent and do
/// not raise, so callers must not depend on this exception for correctness — it is a best-effort safety signal.
/// </summary>
public sealed class ReceiptExpiredException : Exception
{
    public ReceiptExpiredException() : base("The transport receipt has expired or has already been settled.") { }

    public ReceiptExpiredException(string message) : base(message) { }

    public ReceiptExpiredException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed record DestinationDeclaration
{
    public required string Name { get; init; }
    public DestinationRole Role { get; init; } = DestinationRole.Queue;
    public string? Source { get; init; }

    // Provider-specific creation arguments for transports that provision destinations (e.g. RabbitMQ queue arguments).
    // Retry and dead-letter behavior is owned by the core RetryPolicy, not declared here, so destinations stay simple.
    public IReadOnlyDictionary<string, string>? ProviderArguments { get; init; }
}

public sealed record PushOptions
{
    public int MaxConcurrentMessages { get; init; } = 1;
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
}

public interface ITransportInfo
{
    DeliveryGuarantee DeliveryGuarantee { get; }
    OrderingGuarantee Ordering { get; }
    IReadOnlySet<DestinationRole> SupportedRoles { get; }
    int? MaxBatchSize { get; }
    long? MaxMessageBytes { get; }
}

public interface IMessageTransport : IAsyncDisposable
{
    Task<SendResult> SendAsync(string destination, IReadOnlyList<TransportMessage> messages, TransportSendOptions options, CancellationToken ct = default);
    Task CompleteAsync(TransportEntry entry, CancellationToken ct = default);
    Task AbandonAsync(TransportEntry entry, CancellationToken ct = default);
}

public interface ISupportsPull : IMessageTransport
{
    Task<IReadOnlyList<TransportEntry>> ReceiveAsync(string source, ReceiveRequest request, CancellationToken ct);
}

public interface ISupportsPush : IMessageTransport
{
    Task<IPushSubscription> SubscribeAsync(string source, Func<TransportEntry, CancellationToken, Task> onMessage, PushOptions options, CancellationToken ct);
}

public interface ISupportsRedeliveryDelay : IMessageTransport
{
    // The longest redelivery delay the transport can honor natively (e.g. SQS serves this via ChangeMessageVisibility,
    // capped at 12 hours). Null means unbounded. A requested delay longer than this is routed through the runtime-store
    // fallback instead of being silently clamped by the broker.
    TimeSpan? MaxRedeliveryDelay { get; }

    Task AbandonAsync(TransportEntry entry, TimeSpan redeliveryDelay, CancellationToken ct);
}

public interface ISupportsDeadLetter : IMessageTransport
{
    Task DeadLetterAsync(TransportEntry entry, string? reason, CancellationToken ct);

    // Reads dead-lettered entries for a destination so callers can inspect raw payloads (including poison messages
    // that never deserialized) and the dead-letter reason header. Read entries are removed from the dead-letter store.
    Task<IReadOnlyList<TransportEntry>> ReceiveDeadLetteredAsync(string destination, ReceiveRequest request, CancellationToken ct);
}

public interface ISupportsLockRenewal : IMessageTransport
{
    Task RenewLockAsync(TransportEntry entry, TimeSpan? duration, CancellationToken ct);
}

public interface ISupportsVisibilityTimeout : IMessageTransport
{
    // The longest receive visibility timeout the transport can honor natively (e.g. SQS caps visibility at 12 hours).
    // Null means unbounded. Callers requesting a longer visibility than the broker supports should treat that as
    // unsatisfiable rather than relying on a silently clamped value.
    TimeSpan? MaxVisibilityTimeout { get; }

    Task<IReadOnlyList<TransportEntry>> ReceiveAsync(string source, ReceiveRequest request, TimeSpan visibility, CancellationToken ct);
}

public interface ISupportsStats : IMessageTransport
{
    Task<MessageDestinationStats> GetStatsAsync(string destination, CancellationToken ct);
}

public interface ISupportsPriority : IMessageTransport { }

public interface ISupportsDelayedDelivery : IMessageTransport
{
    // The longest delivery delay the transport can honor natively (e.g. SQS caps DelaySeconds at 15 minutes).
    // Null means unbounded. A send scheduled further out than this is routed through the runtime-store fallback
    // instead of being silently truncated to the broker's maximum.
    TimeSpan? MaxDeliveryDelay { get; }
}

public interface ISupportsExpiration : IMessageTransport { }

public interface ISupportsProvisioning : IMessageTransport
{
    Task EnsureAsync(IReadOnlyList<DestinationDeclaration> declarations, CancellationToken ct);
    Task DeleteAsync(string name, CancellationToken ct);
    Task<bool> ExistsAsync(string name, CancellationToken ct);
}

public interface IPushSubscription : IAsyncDisposable
{
    string Source { get; }
}

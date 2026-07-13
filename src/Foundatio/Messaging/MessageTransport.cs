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

/// <summary>
/// The canonical identity of a transport destination: a name, the role that names the physical namespace it lives in,
/// and — for subscriptions — the owning topic. Every transport API (send, receive, subscribe, stats, settlement,
/// provisioning) uses this one value, so the same logical destination can never be spelled two ways on two paths.
/// </summary>
/// <remarks>
/// <see cref="Key"/> is the destination's opaque string form (<c>"{topic}/{name}"</c> for subscriptions, <c>Name</c>
/// otherwise) for logging, metrics tags, and dictionary keys. Because a subscription key contains <c>'/'</c>, a
/// transport must NOT assume it is a legal broker resource name (e.g. an SQS queue name) — map it to native resources
/// during <see cref="ISupportsProvisioning.EnsureAsync"/> and treat it as a lookup key thereafter. Topic and
/// subscription names must not contain <c>'/'</c>.
/// </remarks>
public sealed record DestinationAddress
{
    public required string Name { get; init; }
    public DestinationRole Role { get; init; } = DestinationRole.Queue;

    /// <summary>The owning topic when <see cref="Role"/> is <see cref="DestinationRole.Subscription"/>; null otherwise.</summary>
    public string? Topic { get; init; }

    /// <summary>The canonical opaque string form: <c>"{topic}/{name}"</c> for subscriptions, <c>Name</c> otherwise.</summary>
    public string Key => Topic is { Length: > 0 } topic ? $"{topic}/{Name}" : Name;

    public override string ToString() => $"{Role}:{Key}";

    public static DestinationAddress ForQueue(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return new DestinationAddress { Name = name, Role = DestinationRole.Queue };
    }

    public static DestinationAddress ForTopic(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return new DestinationAddress { Name = name, Role = DestinationRole.Topic };
    }

    public static DestinationAddress ForSubscription(string topic, string subscription)
    {
        ArgumentException.ThrowIfNullOrEmpty(topic);
        ArgumentException.ThrowIfNullOrEmpty(subscription);
        return new DestinationAddress { Name = subscription, Role = DestinationRole.Subscription, Topic = topic };
    }
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
}

/// <summary>
/// One delivered message: the payload and metadata a receive/subscribe hands to the consumer, plus the
/// <see cref="Receipt"/> that settles it (<see cref="IMessageTransport.CompleteAsync"/> /
/// <see cref="IMessageTransport.AbandonAsync(TransportEntry, CancellationToken)"/>).
/// </summary>
/// <remarks>
/// Provider authors: future contract growth only ever adds OPTIONAL init members to this record (never new required
/// ones), so provider code constructing entries stays source-compatible across core upgrades.
/// </remarks>
public sealed record TransportEntry
{
    /// <summary>The broker-assigned message id — stable across redeliveries of the same message.</summary>
    public required string Id { get; init; }

    /// <summary>The source address the entry was received from (the queue or subscription, never the owning topic).</summary>
    public required DestinationAddress Destination { get; init; }

    public required ReadOnlyMemory<byte> Body { get; init; }

    /// <summary>The sent message's headers, which must round-trip byte-for-byte through the transport.</summary>
    public MessageHeaders Headers { get; init; } = MessageHeaders.Empty;

    /// <summary>How many times this message has been delivered, INCLUDING this delivery — starts at 1, never 0.</summary>
    public int DeliveryCount { get; init; } = 1;

    public DateTimeOffset? EnqueuedUtc { get; init; }

    /// <summary>The settlement token for this delivery; see <see cref="Receipt"/>.</summary>
    public required Receipt Receipt { get; init; }
}

/// <summary>
/// The transport's opaque settlement token for one delivery. Everything the transport needs to settle the entry later
/// (complete/abandon/dead-letter) must live in <see cref="TransportState"/> — not in transport instance state keyed by
/// entry identity alone — because the same message can be in flight again (a redelivery) by the time a stale receipt
/// is settled, and per-delivery state is what keeps the two from aliasing.
/// </summary>
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
    /// <summary>The canonical identity of the destination to provision — the SAME address the runtime later sends to,
    /// receives from, and asks stats for, so provisioning and runtime can never disagree on a destination's identity.</summary>
    public required DestinationAddress Address { get; init; }

    // Provider-specific creation arguments for transports that provision destinations (e.g. RabbitMQ queue arguments).
    // Retry and dead-letter behavior is owned by the core RetryPolicy, not declared here, so destinations stay simple.
    public IReadOnlyDictionary<string, string>? ProviderArguments { get; init; }
}

public sealed record PushOptions
{
    public int MaxConcurrentMessages { get; init; } = 1;
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// The capability and limit facts a transport advertises for one <see cref="DestinationRole"/>. Capabilities vary by
/// role on real brokers (SQS queues take DelaySeconds; SNS topics have no native delay), so the core asks per role
/// via <see cref="ITransportInfo.GetCapabilities"/> rather than reading transport-wide flags. Anything not advertised
/// here is treated as unsupported: the core validates, falls back, or throws instead of letting the broker silently
/// drop a requested behavior.
/// </summary>
public sealed record TransportCapabilities
{
    /// <summary>Capabilities of a transport (or role) that advertises nothing: every feature routes through core fallbacks or fails validation.</summary>
    public static readonly TransportCapabilities None = new();

    /// <summary>The destination honors <see cref="TransportSendOptions.DeliverAt"/> natively.</summary>
    public bool DelayedDelivery { get; init; }

    /// <summary>
    /// The longest delivery delay honored natively when <see cref="DelayedDelivery"/> is true (e.g. SQS caps
    /// DelaySeconds at 15 minutes); null means unbounded. A send scheduled further out is routed through the
    /// runtime-store fallback instead of being silently truncated to the broker's ceiling.
    /// </summary>
    public TimeSpan? MaxDeliveryDelay { get; init; }

    /// <summary>The destination honors <see cref="TransportSendOptions.Priority"/>.</summary>
    public bool Priority { get; init; }

    /// <summary>The destination honors per-message expiration (<see cref="KnownHeaders.Expiration"/>).</summary>
    public bool Expiration { get; init; }

    public OrderingGuarantee Ordering { get; init; } = OrderingGuarantee.None;

    /// <summary>Maximum messages per <see cref="IMessageTransport.SendAsync"/> call; null means unbounded. The core chunks larger sends.</summary>
    public int? MaxBatchSize { get; init; }

    /// <summary>Maximum message body size in bytes; null means unbounded. The core rejects oversized messages up front.</summary>
    public long? MaxMessageBytes { get; init; }
}

public interface ITransportInfo
{
    DeliveryGuarantee DeliveryGuarantee { get; }
    IReadOnlySet<DestinationRole> SupportedRoles { get; }

    /// <summary>
    /// The capabilities and limits this transport honors for the given destination. Most transports vary only by
    /// <see cref="DestinationAddress.Role"/> (SQS queues take a native delay; SNS topics do not), but the full address
    /// is the key so a routing/composite transport can answer per destination. Must be side-effect free and cheap;
    /// the core consults it on every send-path decision (native delay vs. runtime-store fallback, priority/expiration
    /// validation, size and batch limits).
    /// </summary>
    TransportCapabilities GetCapabilities(DestinationAddress destination);
}

/// <summary>
/// The provider SPI every transport implements: send messages and settle deliveries. Everything else (pull, push,
/// dead-letter, delays, stats, provisioning) is an optional <c>ISupports*</c> capability interface the core detects
/// at runtime — implement only what the broker actually offers and the core validates or falls back for the rest.
/// </summary>
public interface IMessageTransport : IAsyncDisposable
{
    /// <summary>
    /// Delivers the messages to the destination. Throw-on-failure: any failure throws rather than returning a failed
    /// item, so every item in the returned <see cref="SendResult"/> was accepted. A multi-message send is NOT atomic —
    /// earlier messages may already be delivered when a later one throws.
    /// </summary>
    /// <remarks>
    /// A future <see cref="TransportSendOptions.DeliverAt"/> the transport cannot honor natively must be refused with
    /// <see cref="NotSupportedException"/>, never accepted and delivered immediately (a silently dropped delay); the
    /// core only routes a delayed send here when the destination advertises the
    /// <see cref="TransportCapabilities.DelayedDelivery"/> capability. A topic send with zero subscriptions is
    /// dropped — real pub/sub semantics: subscriptions must exist before a publish can reach them.
    /// </remarks>
    Task<SendResult> SendAsync(DestinationAddress destination, IReadOnlyList<TransportMessage> messages, TransportSendOptions options, CancellationToken ct = default);

    /// <summary>
    /// Permanently removes the delivered entry — the terminal success settlement. Settling with a stale or
    /// already-settled receipt SHOULD throw <see cref="ReceiptExpiredException"/>, but that signal is best-effort:
    /// some brokers (e.g. SQS) treat stale settlement as idempotent, so callers must not depend on it for correctness.
    /// </summary>
    Task CompleteAsync(TransportEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Returns the delivered entry to its source for redelivery with <see cref="TransportEntry.DeliveryCount"/>
    /// incremented. Same stale-receipt semantics as <see cref="CompleteAsync"/>.
    /// </summary>
    Task AbandonAsync(TransportEntry entry, CancellationToken ct = default);
}

public interface ISupportsPull : IMessageTransport
{
    /// <summary>
    /// Receives up to <see cref="ReceiveRequest.MaxMessages"/> entries (a ceiling — fewer, including zero, is valid).
    /// <see cref="ReceiveRequest.MaxWaitTime"/> is a long-poll window: return as soon as any messages arrive, block up
    /// to the window when none are available, and return empty when it lapses. Returned entries carry the source
    /// address as their <see cref="TransportEntry.Destination"/>.
    /// </summary>
    Task<IReadOnlyList<TransportEntry>> ReceiveAsync(DestinationAddress source, ReceiveRequest request, CancellationToken ct = default);
}

public interface ISupportsPush : IMessageTransport
{
    /// <summary>
    /// Attaches a callback that is invoked for each entry delivered from the source until the returned subscription is
    /// disposed. The callback (or the core wrapping it) settles each entry; a callback that throws without settling
    /// must result in the entry being abandoned for redelivery, never lost. At most
    /// <see cref="PushOptions.MaxConcurrentMessages"/> callbacks run concurrently per subscription.
    /// </summary>
    Task<IPushSubscription> SubscribeAsync(DestinationAddress source, Func<TransportEntry, CancellationToken, Task> onMessage, PushOptions options, CancellationToken ct = default);
}

public interface ISupportsRedeliveryDelay : IMessageTransport
{
    // The longest redelivery delay the transport can honor natively (e.g. SQS serves this via ChangeMessageVisibility,
    // capped at 12 hours). Null means unbounded. A requested delay longer than this is routed through the runtime-store
    // fallback instead of being silently clamped by the broker.
    TimeSpan? MaxRedeliveryDelay { get; }

    Task AbandonAsync(TransportEntry entry, TimeSpan redeliveryDelay, CancellationToken ct = default);
}

public interface ISupportsDeadLetter : IMessageTransport
{
    Task DeadLetterAsync(TransportEntry entry, string? reason, CancellationToken ct = default);

    // Reads dead-lettered entries for a destination so callers can inspect raw payloads (including poison messages
    // that never deserialized) and the dead-letter reason header. Read entries are removed from the dead-letter store.
    Task<IReadOnlyList<TransportEntry>> ReceiveDeadLetteredAsync(DestinationAddress destination, ReceiveRequest request, CancellationToken ct = default);
}

public interface ISupportsLockRenewal : IMessageTransport
{
    Task RenewLockAsync(TransportEntry entry, TimeSpan? duration, CancellationToken ct = default);
}

public interface ISupportsVisibilityTimeout : IMessageTransport
{
    // The longest receive visibility timeout the transport can honor natively (e.g. SQS caps visibility at 12 hours).
    // Null means unbounded. Callers requesting a longer visibility than the broker supports should treat that as
    // unsatisfiable rather than relying on a silently clamped value.
    TimeSpan? MaxVisibilityTimeout { get; }

    Task<IReadOnlyList<TransportEntry>> ReceiveAsync(DestinationAddress source, ReceiveRequest request, TimeSpan visibility, CancellationToken ct = default);
}

public interface ISupportsStats : IMessageTransport
{
    Task<MessageDestinationStats> GetStatsAsync(DestinationAddress destination, CancellationToken ct = default);
}

public interface ISupportsProvisioning : IMessageTransport
{
    Task EnsureAsync(IReadOnlyList<DestinationDeclaration> declarations, CancellationToken ct = default);
    Task DeleteAsync(DestinationAddress destination, CancellationToken ct = default);
    Task<bool> ExistsAsync(DestinationAddress destination, CancellationToken ct = default);
}

public interface IPushSubscription : IAsyncDisposable
{
    DestinationAddress Source { get; }
}

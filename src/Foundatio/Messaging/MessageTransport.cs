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
}

public sealed record TransportSendOptions
{
    public MessagePriority Priority { get; init; } = MessagePriority.Normal;
    public DateTimeOffset? DeliverAt { get; init; }
    public string? DeduplicationId { get; init; }
    public string? PartitionKey { get; init; }
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
    public long Queued { get; init; }
    public long Working { get; init; }
    public long Deadletter { get; init; }
    public long Enqueued { get; init; }
    public long Dequeued { get; init; }
    public long Completed { get; init; }
    public long Abandoned { get; init; }
    public long Errors { get; init; }
    public long Timeouts { get; init; }
}

public sealed record SendItemResult
{
    public string? MessageId { get; init; }
    public required bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public bool IsRetryable { get; init; }
}

public sealed record SendResult
{
    public required IReadOnlyList<SendItemResult> Items { get; init; }
    public bool AllSucceeded => Items.All(i => i.Success);
}

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
    Task<IReadOnlyList<TransportEntry>> ReceiveAsync(string source, ReceiveRequest request, TimeSpan visibility, CancellationToken ct);
}

public interface ISupportsStats : IMessageTransport
{
    Task<MessageDestinationStats> GetStatsAsync(string destination, CancellationToken ct);
}

public interface ISupportsPriority : IMessageTransport { }

public interface ISupportsDelayedDelivery : IMessageTransport { }

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

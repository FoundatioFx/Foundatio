using System;
using System.Collections.Generic;

namespace Foundatio.Messaging.Transport;

/// <summary>A decoded record, waiting in a receiver's buffer until it is handed out.</summary>
internal sealed class KafkaRecord
{
    public required string MessageId { get; init; }
    public required ReadOnlyMemory<byte> Body { get; init; }
    public required IReadOnlyDictionary<string, string> Headers { get; init; }
    public string? GroupKey { get; init; }
    public int DeliveryCount { get; init; } = 1;
    public DateTimeOffset? EnqueuedAt { get; init; }

    public KafkaPartitionState? Partition { get; set; }
    public long Offset { get; set; }

    /// <summary>When a locally released record may be handed out again.</summary>
    public DateTimeOffset? AvailableAt { get; set; }
}

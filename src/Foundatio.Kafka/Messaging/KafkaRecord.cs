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
    public int DeliveryCount { get; set; } = 1;
    public DateTimeOffset? EnqueuedAt { get; init; }

    /// <summary>What the record costs in memory, roughly: its key, value and headers, and a fixed overhead.</summary>
    public int Size { get; init; }

    /// <summary>
    /// Released in place, so its delivery count and retry time are known only in memory and must be kept in the state
    /// topic when its partition moves.
    /// </summary>
    public bool Released { get; set; }

    public KafkaPartitionState? Partition { get; set; }
    public long Offset { get; set; }

    /// <summary>When the record may be handed out: a delayed message's delivery time, or a locally released record's retry time.</summary>
    public DateTimeOffset? AvailableAt { get; set; }
}

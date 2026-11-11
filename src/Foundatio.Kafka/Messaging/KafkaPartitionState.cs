using System.Collections.Generic;
using Confluent.Kafka;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// What a consumer has read from one assigned partition and not yet finished. The committed offset is the lowest
/// unfinished offset, so messages can be handled concurrently and out of order without skipping any after a crash.
/// A new instance is created for every assignment, so deliveries from an earlier assignment are recognized as stale.
/// Guarded by the owner's lock.
/// </summary>
internal sealed class KafkaPartitionState
{
    public KafkaPartitionState(TopicPartition topicPartition)
    {
        TopicPartition = topicPartition;
    }

    public TopicPartition TopicPartition { get; }

    /// <summary>Offsets read and not yet finished: buffered, handed out, or released locally.</summary>
    public SortedSet<long> Pending { get; } = [];

    /// <summary>Handed-out deliveries by offset.</summary>
    public Dictionary<long, KafkaIncomingMessage> InFlight { get; } = [];

    /// <summary>The offset after the last one read, or -1 before the first read.</summary>
    public long NextOffset { get; set; } = -1;

    /// <summary>The last offset passed to the consumer's offset store, or -1.</summary>
    public long StoredOffset { get; set; } = -1;

    /// <summary>A rebalance is taking the partition away: nothing more is handed out from it.</summary>
    public bool Revoking { get; set; }

    /// <summary>The partition is no longer owned; settling its deliveries fails with <see cref="MessageLeaseLostException"/>.</summary>
    public bool Revoked { get; set; }

    /// <summary>The offset to commit: the lowest unfinished one, or the next to read when everything read is finished.</summary>
    public long? CommitOffset => Pending.Count > 0 ? Pending.Min : NextOffset >= 0 ? NextOffset : null;

    public void Read(long offset)
    {
        Pending.Add(offset);
        if (offset >= NextOffset)
            NextOffset = offset + 1;
    }
}

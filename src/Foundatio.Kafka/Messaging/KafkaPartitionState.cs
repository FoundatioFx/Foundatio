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

    /// <summary>How many of the partition's records wait in memory for their delivery time.</summary>
    public int TimedCount { get; set; }

    /// <summary>The offset after the last one read, or -1 before the first read.</summary>
    public long NextOffset { get; set; } = -1;

    /// <summary>The last offset passed to the consumer's offset store, or -1.</summary>
    public long StoredOffset { get; set; } = -1;

    /// <summary>A rebalance is taking the partition away: nothing more is handed out from it.</summary>
    public bool Revoking { get; set; }

    /// <summary>The partition is no longer owned; settling its deliveries fails with <see cref="MessageLeaseLostException"/>.</summary>
    public bool Revoked { get; set; }

    /// <summary>The partition's state is being read from the state topic: nothing is handed out from it until it is.</summary>
    public bool Loading { get; set; }

    /// <summary>The state the partition's previous owner kept, while records it describes are still to be read.</summary>
    public KafkaReceiverState? Recovered { get; set; }

    /// <summary>The anchor of <see cref="Recovered"/> was read with its message id, so its finished ranges apply.</summary>
    public bool RecoveredAnchorSeen { get; set; }

    /// <summary>The state topic holds a state for the partition, so an empty state must be written to delete it.</summary>
    public bool StateStored { get; set; }

    /// <summary>The partition's delivery counts or retry times changed since its state was last written.</summary>
    public bool StateChanged { get; set; }

    /// <summary>When the partition's state was last written (a <see cref="System.Diagnostics.Stopwatch"/> timestamp), or 0.</summary>
    public long StateWrittenAt { get; set; }

    /// <summary>The anchor and finished count behind it in the state last written.</summary>
    public (long Anchor, long Finished) StateWrittenRanges { get; set; } = (-1, 0);

    /// <summary>How many offsets after the lowest unfinished one are finished.</summary>
    public long FinishedBehind => Pending.Count > 0 ? NextOffset - Pending.Min - Pending.Count : 0;

    /// <summary>The offset to commit: the lowest unfinished one, or the next to read when everything read is finished.</summary>
    public long? CommitOffset => Pending.Count > 0 ? Pending.Min : NextOffset >= 0 ? NextOffset : null;

    public void Read(long offset)
    {
        Pending.Add(offset);
        if (offset >= NextOffset)
            NextOffset = offset + 1;
    }
}

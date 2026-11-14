using System.Collections.Generic;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// What a receiver knows about one ordered group: its records waiting to be handed out, in the order they were read,
/// how many of its records are handed out, and which of its records wait for their delivery time. Guarded by the
/// receiver's lock.
/// </summary>
internal sealed class KafkaRecordGroup
{
    public KafkaRecordGroup(string key)
    {
        Key = key;
    }

    public string Key { get; }

    /// <summary>The group's records read and not handed out, in the order they were read.</summary>
    public LinkedList<KafkaRecord> Records { get; } = new();

    /// <summary>
    /// The receiver's buffer node for the first of <see cref="Records"/>, while the group can be handed out; the
    /// group's other records are held and are not in the buffer.
    /// </summary>
    public LinkedListNode<KafkaRecord>? Queued { get; set; }

    /// <summary>How many of the group's records are handed out and unsettled.</summary>
    public int InFlight { get; set; }

    /// <summary>The group's records that wait for their delivery time.</summary>
    public List<KafkaRecord> Timed { get; } = [];

    public bool IsEmpty => Records.Count == 0 && InFlight == 0 && Timed.Count == 0;
}

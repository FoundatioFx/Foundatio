using System;
using System.Collections.Generic;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// The records of one partition that a receiver read but did not keep, because their group held its budget: by group,
/// in offset order, with their message ids. Their offsets stay unfinished, so the partition's committed offset never
/// passes them, and <see cref="KafkaCatchUpReader"/> reads them again once their group drains. While a group has
/// skipped records on the partition, its later records there are skipped too, so it stays in order. Guarded by the
/// receiver's lock.
/// </summary>
internal sealed class KafkaSkippedRecords
{
    private static readonly Dictionary<long, KafkaReceiverAttempt> EmptyAttempts = [];

    private readonly Dictionary<string, Queue<(long Offset, string MessageId)>> _groups = new(StringComparer.Ordinal);

    /// <summary>The first skipped offset of each group, and the group's key.</summary>
    private readonly SortedDictionary<long, string> _first = [];

    private Dictionary<long, KafkaReceiverAttempt>? _attempts;

    /// <summary>How many records are skipped.</summary>
    public int Count { get; private set; }

    /// <summary>The lowest skipped offset, or -1 when nothing is skipped.</summary>
    public long LowestOffset { get; private set; } = -1;

    /// <summary>The delivery counts and retry times recovered for skipped records, to apply when they are read again.</summary>
    public IReadOnlyDictionary<long, KafkaReceiverAttempt> Attempts => _attempts ?? EmptyAttempts;

    /// <summary>The first skipped offset of each group with skipped records, in ascending order, with the group's key.</summary>
    public IEnumerable<KeyValuePair<long, string>> FirstOffsets => _first;

    /// <summary>Whether the group has skipped records on the partition.</summary>
    public bool Contains(string key) => _groups.ContainsKey(key);

    /// <summary>The group whose first skipped record is at <paramref name="offset"/>, if any.</summary>
    public bool TryGetGroupAt(long offset, out string key)
    {
        if (_first.TryGetValue(offset, out string? found))
        {
            key = found;
            return true;
        }

        key = String.Empty;
        return false;
    }

    /// <summary>The message id of the first skipped record of a group, at <paramref name="offset"/>.</summary>
    public bool TryGetMessageIdAt(long offset, out string messageId)
    {
        if (_first.TryGetValue(offset, out string? key))
        {
            messageId = _groups[key].Peek().MessageId;
            return true;
        }

        messageId = String.Empty;
        return false;
    }

    /// <summary>Adds a record after the group's skipped ones; offsets must ascend within a group.</summary>
    public void Add(string key, long offset, string messageId, KafkaReceiverAttempt? attempt)
    {
        if (!_groups.TryGetValue(key, out var skipped))
        {
            _groups[key] = skipped = new Queue<(long, string)>();
            _first[offset] = key;
            if (LowestOffset < 0 || offset < LowestOffset)
                LowestOffset = offset;
        }

        skipped.Enqueue((offset, messageId));
        if (attempt is not null)
            (_attempts ??= [])[offset] = attempt;

        Count++;
    }

    /// <summary>Removes the group's first skipped record, returning the attempt recovered for it, if any.</summary>
    public KafkaReceiverAttempt? RemoveFirst(string key)
    {
        var skipped = _groups[key];
        var (offset, _) = skipped.Dequeue();
        _first.Remove(offset);
        if (skipped.Count == 0)
            _groups.Remove(key);
        else
            _first[skipped.Peek().Offset] = key;

        Count--;
        if (offset == LowestOffset)
            LowestOffset = FindLowest();

        KafkaReceiverAttempt? attempt = null;
        if (_attempts is not null && _attempts.Remove(offset, out var found))
            attempt = found;

        return attempt;
    }

    /// <summary>How many records each group has skipped on the partition.</summary>
    public IEnumerable<(string Key, int Count)> CountsByGroup()
    {
        foreach (var (key, skipped) in _groups)
            yield return (key, skipped.Count);
    }

    private long FindLowest()
    {
        foreach (var (offset, _) in _first)
            return offset;

        return -1;
    }
}

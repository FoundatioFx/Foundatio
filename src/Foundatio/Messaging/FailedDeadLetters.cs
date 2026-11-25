using System;
using System.Collections.Generic;
using System.Threading;

namespace Foundatio.Messaging;

/// <summary>
/// The messages this processor could not dead-letter. When one is delivered again, its dead-letter is retried without
/// running its handlers again. Bounded: when full, the oldest entry is forgotten.
/// </summary>
internal sealed class FailedDeadLetters
{
    private const int Capacity = 1000;

    private readonly object _lock = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _order = new();
    private int _count;

    /// <summary>Returns the reason the message was to be dead-lettered with, if dead-lettering it failed earlier.</summary>
    public bool TryGetReason(string messageId, out string reason)
    {
        reason = String.Empty;
        if (Volatile.Read(ref _count) == 0)
            return false;

        lock (_lock)
        {
            if (!_entries.TryGetValue(messageId, out var node))
                return false;

            reason = node.Value.Reason;
            return true;
        }
    }

    /// <summary>Records a failed dead-letter and returns how many times dead-lettering this message has failed.</summary>
    public int RecordFailure(string messageId, string reason)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(messageId, out var node))
                return ++node.Value.Failures;

            if (_entries.Count >= Capacity && _order.First is { } oldest)
            {
                _entries.Remove(oldest.Value.MessageId);
                _order.RemoveFirst();
            }

            _entries[messageId] = _order.AddLast(new Entry(messageId, reason) { Failures = 1 });
            Volatile.Write(ref _count, _entries.Count);
            return 1;
        }
    }

    public void Remove(string messageId)
    {
        if (Volatile.Read(ref _count) == 0)
            return;

        lock (_lock)
        {
            if (!_entries.Remove(messageId, out var node))
                return;

            _order.Remove(node);
            Volatile.Write(ref _count, _entries.Count);
        }
    }

    private sealed class Entry(string messageId, string reason)
    {
        public string MessageId { get; } = messageId;
        public string Reason { get; } = reason;
        public int Failures { get; set; }
    }
}

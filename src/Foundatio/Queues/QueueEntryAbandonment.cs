using System;
using System.Threading;

namespace Foundatio.Queues;

// A shared entry flag cannot distinguish this call from a competing abandonment.
internal sealed class QueueEntryAbandonment : IDisposable
{
    private static readonly AsyncLocal<QueueEntryAbandonment?> _current = new();
    private readonly QueueEntryAbandonment? _previous;
    private readonly IQueueEntry _entry;
    private Exception? _observerError;

    public QueueEntryAbandonment(IQueueEntry entry)
    {
        _entry = entry;
        _previous = _current.Value;
        _current.Value = this;
    }

    public bool ObserverThrew(Exception error) => ReferenceEquals(error, _observerError);

    public static void RecordObserverError(IQueueEntry entry, Exception error)
    {
        var attempt = _current.Value;
        if (attempt is not null && ReferenceEquals(attempt._entry, entry) && entry.IsAbandoned)
            attempt._observerError = error;
    }

    public void Dispose() => _current.Value = _previous;
}

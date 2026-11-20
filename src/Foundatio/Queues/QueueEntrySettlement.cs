using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Queues;

internal sealed class QueueEntrySettlement<T> : IDisposable where T : class
{
    private static readonly AsyncLocal<WeakReference<QueueEntrySettlement<T>>?> _current = new();
    private readonly WeakReference<QueueEntrySettlement<T>>? _previous;
    private readonly IQueueEntry<T> _entry;
    private readonly bool _completing;
    private QueueEntry<T>? _providerEntry;
    private Task? _providerTask;
    private volatile bool _confirmed;
    private volatile bool _closed;

    public QueueEntrySettlement(IQueueEntry<T> entry, bool completing)
    {
        _entry = entry;
        _completing = completing;
        _previous = _current.Value;
        // A retry timer or observer's background task can outlive the caller.
        _current.Value = new WeakReference<QueueEntrySettlement<T>>(this);
    }

    private static QueueEntrySettlement<T>? GetActive(WeakReference<QueueEntrySettlement<T>>? scope) =>
        scope?.TryGetTarget(out var attempt) == true && !attempt._closed ? attempt : null;

    public bool IsConfirmedFor(Task? operation)
    {
        if (!_confirmed)
            return false;

        var providerEntry = Volatile.Read(ref _providerEntry);
        if (providerEntry is null || ReferenceEquals(_entry, providerEntry))
            return true;

        // A forwarding entry may keep its own metadata and return the native operation directly.
        if (operation is not null && ReferenceEquals(operation, Volatile.Read(ref _providerTask)))
            return _completing ? _entry.IsCompleted : _entry.IsAbandoned;

        return QueueEntry<T>.IsSameDelivery(_entry, providerEntry);
    }

    public static Task InvokeProviderAsync(IQueue<T> queue, QueueEntry<T> entry, bool completing)
    {
        var previous = _current.Value;
        if (previous is null)
            return completing ? queue.CompleteAsync(entry) : queue.AbandonAsync(entry);

        var attempt = GetActive(previous);
        // A nested entry call owns its settlement, even when it inherits the outer call's execution context.
        bool suppress = attempt is not null && !attempt.TryEnterProvider(entry, completing);
        if (suppress)
            _current.Value = null;

        try
        {
            var task = completing ? queue.CompleteAsync(entry) : queue.AbandonAsync(entry);
            if (attempt is not null && !suppress)
                Volatile.Write(ref attempt._providerTask, task);

            return task;
        }
        finally
        {
            if (suppress)
                _current.Value = previous;
        }
    }

    private bool TryEnterProvider(QueueEntry<T> entry, bool completing)
    {
        if (_completing != completing || (_entry is QueueEntry<T> native && !ReferenceEquals(native, entry)))
            return false;

        return Interlocked.CompareExchange(ref _providerEntry, entry, null) is null;
    }

    public static void Confirm(IQueueEntry<T> entry, bool completing)
    {
        var attempt = GetActive(_current.Value);
        if (attempt is null || attempt._confirmed || attempt._completing != completing)
            return;

        var expected = Volatile.Read(ref attempt._providerEntry) ?? attempt._entry;
        if (QueueEntry<T>.IsSameDelivery(expected, entry) && (completing ? entry.IsCompleted : entry.IsAbandoned))
            attempt._confirmed = true;
    }

    public void Dispose()
    {
        _closed = true;
        _current.Value = _previous;
    }
}

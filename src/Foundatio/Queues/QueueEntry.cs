using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Queues;

public class QueueEntry<T> : IQueueEntry<T>, IQueueEntryMetadata, IAsyncDisposable where T : class
{
    private readonly IQueue<T> _queue;
    private readonly T _original;

    public QueueEntry(string id, string correlationId, T value, IQueue<T> queue, DateTime enqueuedTimeUtc, int attempts)
    {
        Id = id;
        CorrelationId = correlationId;
        _original = value;
        Value = value.DeepClone();
        _queue = queue;
        EnqueuedTimeUtc = enqueuedTimeUtc;
        Attempts = attempts;
        DequeuedTimeUtc = RenewedTimeUtc = _queue.GetTimeProvider().GetUtcNow().UtcDateTime;
    }

    public string Id { get; }
    public string CorrelationId { get; }
    public IDictionary<string, string> Properties { get; } = new Dictionary<string, string>();
    public bool IsCompleted { get; private set; }
    public bool IsAbandoned { get; private set; }
    public Type EntryType => Value.GetType();
    public object GetValue() => Value;
    public T Value { get; set; }
    public DateTime EnqueuedTimeUtc { get; set; }
    public DateTime RenewedTimeUtc { get; set; }
    public DateTime DequeuedTimeUtc { get; set; }
    public int Attempts { get; set; }
    public TimeSpan ProcessingTime { get; set; }
    public TimeSpan TotalTime { get; set; }

    void IQueueEntry.MarkCompleted()
    {
        IsCompleted = true;
    }

    void IQueueEntry.MarkAbandoned()
    {
        IsAbandoned = true;
    }

    public Task RenewLockAsync()
    {
        RenewedTimeUtc = _queue.GetTimeProvider().GetUtcNow().UtcDateTime;
        return _queue.RenewLockAsync(this);
    }

    public Task CompleteAsync()
    {
        return _queue.CompleteAsync(this);
    }

    public Task AbandonAsync()
    {
        return _queue.AbandonAsync(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (!IsAbandoned && !IsCompleted)
            await AbandonAsync();
    }

    internal void Reset()
    {
        IsCompleted = false;
        IsAbandoned = false;
        Value = _original.DeepClone();
    }
}

public interface IQueueEntryMetadata
{
    string Id { get; }
    string CorrelationId { get; }
    IDictionary<string, string> Properties { get; }
    DateTime EnqueuedTimeUtc { get; }
    DateTime RenewedTimeUtc { get; }
    DateTime DequeuedTimeUtc { get; }
    int Attempts { get; }
    TimeSpan ProcessingTime { get; }
    TimeSpan TotalTime { get; }
}

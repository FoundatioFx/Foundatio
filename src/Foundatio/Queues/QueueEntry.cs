using System;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Queues {
    public class QueueEntry<T> : IQueueEntry<T>, IQueueEntryMetadata, IAsyncDisposable where T : class {
        private readonly IQueue<T> _queue;

        public QueueEntry(string id, T value, IQueue<T> queue, DateTime enqueuedTimeUtc, int attempts) {
            Id = id;
            Value = value;
            _queue = queue;
            EnqueuedTimeUtc = enqueuedTimeUtc;
            Attempts = attempts;
            DequeuedTimeUtc = RenewedTimeUtc = SystemClock.UtcNow;
        }

        public string Id { get; }
        public bool IsCompleted { get; private set; }
        public bool IsAbandoned { get; private set; }
        public T Value { get; set; }
        public DateTime EnqueuedTimeUtc { get; set; }
        public DateTime RenewedTimeUtc { get; set; }
        public DateTime DequeuedTimeUtc { get; set; }
        public int Attempts { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public DataDictionary Data { get; } = new DataDictionary();

        void IQueueEntry<T>.MarkCompleted() {
            IsCompleted = true;
        }

        void IQueueEntry<T>.MarkAbandoned() {
            IsAbandoned = true;
        }

        public Task RenewLockAsync() {
            RenewedTimeUtc = SystemClock.UtcNow;
            return _queue.RenewLockAsync(this);
        }

        public Task CompleteAsync() {
            return _queue.CompleteAsync(this);
        }

        public Task AbandonAsync() {
            return _queue.AbandonAsync(this);
        }

        public Task DisposeAsync() {
            if (!IsAbandoned && !IsCompleted)
                return AbandonAsync();

            return Task.CompletedTask;
        }
    }

    public interface IQueueEntryMetadata {
        DateTime EnqueuedTimeUtc { get; }
        DateTime RenewedTimeUtc { get; }
        DateTime DequeuedTimeUtc { get; }
        int Attempts { get; }
        TimeSpan ProcessingTime { get; }
        DataDictionary Data { get; }
    }
}
using System;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Utility;

namespace Foundatio.Queues {
    public class QueueEntry<T> : IDisposable, IQueueEntry<T>, IQueueEntryMetadata where T : class {
        private readonly IQueue<T> _queue;
        private bool _isAbandoned;
        private bool _isCompleted;

        public QueueEntry(string id, T value, IQueue<T> queue, DateTime enqueuedTimeUtc, int attempts) {
            Id = id;
            Value = value;
            _queue = queue;
            EnqueuedTimeUtc = enqueuedTimeUtc;
            Attempts = attempts;
            DequeuedTimeUtc = RenewedTimeUtc = DateTime.UtcNow;
        }

        public string Id { get; }
        public T Value { get; set; }
        public DateTime EnqueuedTimeUtc { get; set; }
        public DateTime RenewedTimeUtc { get; set; }
        public DateTime DequeuedTimeUtc { get; set; }
        public int Attempts { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public DataDictionary Data { get; } = new DataDictionary();

        public Task RenewLockAsync() {
            RenewedTimeUtc = DateTime.UtcNow;
            return _queue.RenewLockAsync(this);
        }

        public Task CompleteAsync() {
            if (_isAbandoned || _isCompleted)
                return TaskHelper.Completed();

            _isCompleted = true;
            return _queue.CompleteAsync(this);
        }

        public Task AbandonAsync() {
            if (_isAbandoned || _isCompleted)
                return TaskHelper.Completed();

            _isAbandoned = true;
            return _queue.AbandonAsync(this);
        }

        public virtual async void Dispose() {
            if (!_isAbandoned && !_isCompleted)
                await AbandonAsync().AnyContext();
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
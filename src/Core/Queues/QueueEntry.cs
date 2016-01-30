using System;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Utility;

namespace Foundatio.Queues {
    public class QueueEntry<T> : IDisposable, IQueueEntry<T>, IQueueEntryMetadata where T : class {
        private readonly IQueue<T> _queue;
        private bool _isCompleted;

        public QueueEntry(string id, T value, IQueue<T> queue, DateTime enqueuedTimeUtc, int attempts) {
            Id = id;
            Value = value;
            _queue = queue;
            EnqueuedTimeUtc = enqueuedTimeUtc;
            Attempts = attempts;
            DequeuedTimeUtc = DateTime.UtcNow;
        }

        public string Id { get; }
        public T Value { get; private set; }
        public DateTime EnqueuedTimeUtc { get; }
        public DateTime DequeuedTimeUtc { get; }
        public int Attempts { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public DataDictionary Data { get; } = new DataDictionary();

        public Task CompleteAsync() {
            if (_isCompleted)
                return TaskHelper.Completed();

            _isCompleted = true;
            return _queue.CompleteAsync(this);
        }

        public Task AbandonAsync() {
            return _queue.AbandonAsync(this);
        }

        public virtual async void Dispose() {
            if (!_isCompleted)
                await AbandonAsync().AnyContext();
        }
    }

    public interface IQueueEntryMetadata {
        DateTime EnqueuedTimeUtc { get; }
        DateTime DequeuedTimeUtc { get; }
        int Attempts { get; }
        TimeSpan ProcessingTime { get; }
        DataDictionary Data { get; }
    }
}
using System;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Utility;

namespace Foundatio.Queues {
    public class QueueEntry<T> : IDisposable where T : class {
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

        public Task CompleteAsync() {
            if (_isCompleted)
                return TaskHelper.Completed();

            _isCompleted = true;
            return _queue.CompleteAsync(Id);
        }

        public Task AbandonAsync() {
            return _queue.AbandonAsync(Id);
        }

        public virtual void Dispose() {
            if (!_isCompleted)
                AbandonAsync().AnyContext().GetAwaiter().GetResult();
        }

        public QueueEntryMetadata ToMetadata() {
            return new QueueEntryMetadata {
                Id = Id,
                EnqueuedTimeUtc = EnqueuedTimeUtc,
                DequeuedTimeUtc = DequeuedTimeUtc,
                Attempts = Attempts
            };
        }
    }
    
    public class QueueEntryMetadata {
        public QueueEntryMetadata() {
            Data = new DataDictionary();
        }

        public string Id { get; set; }
        public DateTime EnqueuedTimeUtc { get; set; }
        public DateTime DequeuedTimeUtc { get; set; }
        public int Attempts { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public DataDictionary Data { get; set; } 
    }
}
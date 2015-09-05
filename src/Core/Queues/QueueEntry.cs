using System;
using Foundatio.Utility;

namespace Foundatio.Queues {
    public class QueueEntry<T> : IDisposable where T: class {
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

        public void Complete() {
            if (_isCompleted)
                return;

            _isCompleted = true;
            _queue.Complete(Id);
        }

        public void Abandon() {
            _queue.Abandon(Id);
        }

        public virtual void Dispose() {
            if (!_isCompleted)
                Abandon();
        }

        public QueueEntryMetadata ToMetadata()
        {
            return new QueueEntryMetadata
            {
                Id = Id,
                EnqueuedTimeUtc = EnqueuedTimeUtc,
                DequeuedTimeUtc = DequeuedTimeUtc,
                Attempts = Attempts
            };
        }
    }

    public class QueueEntryMetadata
    {
        public QueueEntryMetadata()
        {
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
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Foundatio.Queues {
    public class QueueEntry<T> : IQueueEntryMetadata, IDisposable where T: class {
        private readonly IQueue<T> _queue;
        private bool _isCompleted;
        private readonly Stopwatch _processingTimer;

        public QueueEntry(string id, T value, IQueue<T> queue, DateTime enqueuedTimeUtc, int attempts) {
            Id = id;
            Value = value;
            _queue = queue;
            EnqueuedTimeUtc = enqueuedTimeUtc;
            Attempts = attempts;
            DequeuedTimeUtc = DateTime.UtcNow;
            _processingTimer = new Stopwatch();
            _processingTimer.Start();
        }

        public string Id { get; }
        public T Value { get; private set; }
        public DateTime EnqueuedTimeUtc { get; }
        public DateTime DequeuedTimeUtc { get; }
        public int Attempts { get; set; }
        public TimeSpan ProcessingTime => _processingTimer.Elapsed;

        public void Complete() {
            if (_isCompleted)
                return;

            _isCompleted = true;
            _processingTimer.Stop();
            _queue.Complete(this);
        }

        public void Abandon() {
            _processingTimer.Stop();
            _queue.Abandon(this);
        }

        public virtual void Dispose() {
            if (!_isCompleted)
                Abandon();
        }
    }

    public interface IQueueEntryMetadata
    {
        string Id { get; }
        DateTime EnqueuedTimeUtc { get; }
        DateTime DequeuedTimeUtc { get; }
        int Attempts { get; }
        TimeSpan ProcessingTime { get; }
    }

    public class QueueEntry2<T> : IDisposable where T : class {
        private readonly IQueue2<T> _queue;
        private bool _isCompleted;

        public QueueEntry2(string id, T value, IQueue2<T> queue) {
            Id = id;
            Value = value;
            _queue = queue;
        }

        public string Id { get; }
        public T Value { get; private set; }

        public async Task CompleteAsync() {
            if (_isCompleted)
                return;

            _isCompleted = true;
            await _queue.CompleteAsync(Id);
        }

        public async Task AbandonAsync() {
            await _queue.AbandonAsync(Id);
        }

        public virtual void Dispose() {
            if (!_isCompleted)
               AbandonAsync().Wait();
        }
    }
}
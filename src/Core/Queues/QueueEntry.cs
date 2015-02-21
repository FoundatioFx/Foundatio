using System;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Queues {
    public class QueueEntry<T> : IDisposable where T: class {
        private readonly IQueue<T> _queue;
        private bool _isCompleted;

        public QueueEntry(string id, T value, IQueue<T> queue) {
            Id = id;
            Value = value;
            _queue = queue;
        }

        public string Id { get; private set; }

        public T Value { get; private set; }

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
    }

    public class QueueEntry2<T> : IDisposable where T : class {
        private readonly IQueue2<T> _queue;
        private bool _isCompleted;

        public QueueEntry2(string id, T value, IQueue2<T> queue) {
            Id = id;
            Value = value;
            _queue = queue;
        }

        public string Id { get; private set; }

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
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Foundatio.Metrics;

namespace Foundatio.Queues {
    public class QueueEntry<T> : IDisposable where T: class {
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

        public async Task CompleteAsync() {
            if (_isCompleted)
                return;

            _isCompleted = true;
            _processingTimer.Stop();
            await _queue.CompleteAsync(this).AnyContext();
        }

        public async Task AbandonAsync() {
            _processingTimer.Stop();
            await _queue.AbandonAsync(this).AnyContext();
        }

        public virtual void Dispose() {
            if (!_isCompleted)
                AbandonAsync().AnyContext().GetAwaiter().GetResult();
        }

        public QueueEntryMetadata ToMetadata() {
            return new QueueEntryMetadata
            {
                Id = Id,
                EnqueuedTimeUtc = EnqueuedTimeUtc,
                DequeuedTimeUtc = DequeuedTimeUtc,
                Attempts = Attempts,
                ProcessingTime = ProcessingTime
            };
        }
}
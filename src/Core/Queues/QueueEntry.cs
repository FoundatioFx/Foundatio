using System;
using System.Collections.Generic;
using System.Diagnostics;
using Foundatio.Metrics;
using Foundatio.Utility;

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

        public void Complete() {
            if (_isCompleted)
                return;

            _isCompleted = true;
            _processingTimer.Stop();
            _queue.Complete(Id);
        }

        public void Abandon() {
            _processingTimer.Stop();
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
                Attempts = Attempts,
                ProcessingTime = ProcessingTime
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
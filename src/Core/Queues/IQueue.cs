using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using Foundatio.Serializer;

namespace Foundatio.Queues {
    public interface IQueue<T> : IHaveSerializer, IDisposable where T : class
    {
        void AttachBehavior(IQueueBehavior<T> behavior);

        string Enqueue(T data);

        void StartWorking(Action<QueueEntry<T>> handler, bool autoComplete = false, CancellationToken token = default(CancellationToken));

        QueueEntry<T> Dequeue(TimeSpan? timeout = null, CancellationToken cancellationToken = default(CancellationToken));
        void Complete(string id);
        void Abandon(string id);

        IEnumerable<T> GetDeadletterItems();

        event EventHandler<EnqueuingEventArgs<T>> Enqueuing;
        event EventHandler<EnqueuedEventArgs<T>> Enqueued;
        event EventHandler<DequeuedEventArgs<T>> Dequeued;
        event EventHandler<CompletedEventArgs<T>> Completed;
        event EventHandler<AbandonedEventArgs<T>> Abandoned;

        void DeleteQueue();
        QueueStats GetQueueStats();
        string QueueId { get; }
    }

    public class QueueStats {
        public long Queued { get; set; }
        public long Working { get; set; }
        public long Deadletter { get; set; }
        public long Enqueued { get; set; }
        public long Dequeued { get; set; }
        public long Completed { get; set; }
        public long Abandoned { get; set; }
        public long Errors { get; set; }
        public long Timeouts { get; set; }
    }

    public class EnqueuingEventArgs<T> : CancelEventArgs where T : class
    {
        public IQueue<T> Queue { get; set; }
        public T Data { get; set; }
    }

    public class EnqueuedEventArgs<T> : EventArgs where T : class
    {
        public IQueue<T> Queue { get; set; }
        public QueueEntryMetadata Metadata { get; set; }
        public T Data { get; set; }
    }

    public class DequeuedEventArgs<T> : EventArgs where T : class
    {
        public IQueue<T> Queue { get; set; }
        public T Data { get; set; }
        public QueueEntryMetadata Metadata { get; set; }
    }

    public class CompletedEventArgs<T> : EventArgs where T : class
    {
        public IQueue<T> Queue { get; set; }
        public QueueEntryMetadata Metadata { get; set; }
    }

    public class AbandonedEventArgs<T> : EventArgs where T : class
    {
        public IQueue<T> Queue { get; set; }
        public QueueEntryMetadata Metadata { get; set; }
    }
}

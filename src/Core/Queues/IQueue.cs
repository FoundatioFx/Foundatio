using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Serializer;

namespace Foundatio.Queues {
    public interface IQueue<T> : IHaveSerializer, IDisposable where T : class {
        event EventHandler<EnqueuingEventArgs<T>> Enqueuing;
        event EventHandler<EnqueuedEventArgs<T>> Enqueued;
        event EventHandler<DequeuedEventArgs<T>> Dequeued;
        event EventHandler<CompletedEventArgs<T>> Completed;
        event EventHandler<AbandonedEventArgs<T>> Abandoned;

        void AttachBehavior(IQueueBehavior<T> behavior);

        Task<string> EnqueueAsync(T data);

        Task StartWorkingAsync(Func<QueueEntry<T>, Task> handler, bool autoComplete = false, CancellationToken cancellationToken = default(CancellationToken));

        Task StopWorkingAsync();

        Task<QueueEntry<T>> DequeueAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default(CancellationToken));

        Task CompleteAsync(IQueueEntryMetadata entry);

        Task AbandonAsync(IQueueEntryMetadata entry);
        
        Task<IEnumerable<T>> GetDeadletterItemsAsync(CancellationToken cancellationToken = default(CancellationToken));

        Task<QueueStats> GetQueueStatsAsync();

        Task DeleteQueueAsync();

        string QueueId { get; }
    }

    public interface IQueueManager {
        Task<IQueue<T>> CreateQueueAsync<T>(string name = null, object config = null) where T : class;

        Task DeleteQueueAsync(string name);

        Task ClearQueueAsync(string name);
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

    public class EnqueuingEventArgs<T> : CancelEventArgs where T : class {
        public IQueue<T> Queue { get; set; }
        public T Data { get; set; }
    }

    public class EnqueuedEventArgs<T> : EventArgs where T : class {
        public IQueue<T> Queue { get; set; }
        public string Id { get; set; }
        public T Data { get; set; }
    }

    public class DequeuedEventArgs<T> : EventArgs where T : class {
        public IQueue<T> Queue { get; set; }
        public T Data { get; set; }
        public IQueueEntryMetadata Metadata { get; set; }
    }

    public class CompletedEventArgs<T> : EventArgs where T : class {
        public IQueue<T> Queue { get; set; }
        public IQueueEntryMetadata Metadata { get; set; }
    }

    public class AbandonedEventArgs<T> : EventArgs where T : class {
        public IQueue<T> Queue { get; set; }
        public IQueueEntryMetadata Metadata { get; set; }
    }
}
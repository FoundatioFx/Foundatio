using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Serializer;
using Foundatio.Utility;

namespace Foundatio.Queues {
    public interface IQueue<T> : IHaveSerializer, IDisposable where T : class {
        AsyncEvent<EnqueuingEventArgs<T>> Enqueuing { get; }
        AsyncEvent<EnqueuedEventArgs<T>> Enqueued { get; }
        AsyncEvent<DequeuedEventArgs<T>> Dequeued { get; }
        AsyncEvent<CompletedEventArgs<T>> Completed { get; }
        AsyncEvent<AbandonedEventArgs<T>> Abandoned { get; }

        void AttachBehavior(IQueueBehavior<T> behavior);

        Task<string> EnqueueAsync(T data);

        Task<QueueEntry<T>> DequeueAsync(CancellationToken cancellationToken = default(CancellationToken));

        [Obsolete("Use QueueEntry<T> overload")]
        Task CompleteAsync(string id);

        Task CompleteAsync(QueueEntry<T> queueEntry);

        [Obsolete("Use QueueEntry<T> overload")]
        Task AbandonAsync(string id);

        Task AbandonAsync(QueueEntry<T> queueEntry);

        Task<IEnumerable<T>> GetDeadletterItemsAsync(CancellationToken cancellationToken = default(CancellationToken));

        Task<QueueStats> GetQueueStatsAsync();

        Task DeleteQueueAsync();

        void StartWorking(Func<QueueEntry<T>, CancellationToken, Task> handler, bool autoComplete = false, CancellationToken cancellationToken = default(CancellationToken));

        string QueueId { get; }
    }
    
    public static class QueueExtensions {
        public static void StartWorking<T>(this IQueue<T> queue, Func<QueueEntry<T>, Task> handler, bool autoComplete = false, CancellationToken cancellationToken = default(CancellationToken)) where T : class {
            queue.StartWorking((entry, token) => handler(entry), autoComplete, cancellationToken);
        }

        public static Task<QueueEntry<T>> DequeueAsync<T>(this IQueue<T> queue, TimeSpan? timeout = null) where T : class {
            return queue.DequeueAsync(timeout.ToCancellationToken(TimeSpan.FromSeconds(30)));
        }
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
        public QueueEntryMetadata Metadata { get; set; }
        public T Data { get; set; }
    }

    public class DequeuedEventArgs<T> : EventArgs where T : class {
        public IQueue<T> Queue { get; set; }
        public T Data { get; set; }
        public QueueEntryMetadata Metadata { get; set; }
    }

    public class CompletedEventArgs<T> : EventArgs where T : class {
        public IQueue<T> Queue { get; set; }
        public QueueEntryMetadata Metadata { get; set; }
    }

    public class AbandonedEventArgs<T> : EventArgs where T : class {
        public IQueue<T> Queue { get; set; }
        public QueueEntryMetadata Metadata { get; set; }
    }
}
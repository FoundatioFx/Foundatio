﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Serializer;
using Foundatio.Utility;

namespace Foundatio.Queues {
    public interface IQueue<T> : IQueue where T : class {
        AsyncEvent<EnqueuingEventArgs<T>> Enqueuing { get; }
        AsyncEvent<EnqueuedEventArgs<T>> Enqueued { get; }
        AsyncEvent<DequeuedEventArgs<T>> Dequeued { get; }
        AsyncEvent<LockRenewedEventArgs<T>> LockRenewed { get; }
        AsyncEvent<CompletedEventArgs<T>> Completed { get; }
        AsyncEvent<AbandonedEventArgs<T>> Abandoned { get; }

        void AttachBehavior(IQueueBehavior<T> behavior);
        Task<string> EnqueueAsync(T data, QueueOptions options = null);
        Task<IQueueEntry<T>> DequeueAsync(CancellationToken cancellationToken);
        Task<IQueueEntry<T>> DequeueAsync(TimeSpan? timeout = null);
        Task RenewLockAsync(IQueueEntry<T> queueEntry);
        Task CompleteAsync(IQueueEntry<T> queueEntry);
        Task AbandonAsync(IQueueEntry<T> queueEntry);
        Task<IEnumerable<T>> GetDeadletterItemsAsync(CancellationToken cancellationToken = default);
        /// <summary>
        ///     Asynchronously dequeues entries in the background.
        /// </summary>
        /// <param name="handler">
        ///     Function called on entry dequeued.
        /// </param>
        /// <param name="autoComplete">
        ///     True to call <see cref="CompleteAsync"/> after the <paramref name="handler"/> is run,
        ///     defaults to false.
        /// </param>
        /// <param name="cancellationToken">
        ///     The token used to cancel the background worker.
        /// </param>
        Task StartWorkingAsync(Func<IQueueEntry<T>, CancellationToken, Task> handler, bool autoComplete = false, CancellationToken cancellationToken = default);
    }

    public interface IQueue : IHaveSerializer, IDisposable {
        Task<QueueStats> GetQueueStatsAsync();
        Task DeleteQueueAsync();
        string QueueId { get; }
    }

    public static class QueueExtensions {
        public static Task StartWorkingAsync<T>(this IQueue<T> queue, Func<IQueueEntry<T>, Task> handler, bool autoComplete = false, CancellationToken cancellationToken = default) where T : class
            => queue.StartWorkingAsync((entry, token) => handler(entry), autoComplete, cancellationToken);
    }

    [DebuggerDisplay("Queued={Queued}, Working={Working}, Deadletter={Deadletter}, Enqueued={Enqueued}, Dequeued={Dequeued}, Completed={Completed}, Abandoned={Abandoned}, Errors={Errors}, Timeouts={Timeouts}")]
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

    public class QueueOptions {
        public string CorrelationId { get; set; }
        public string ParentId { get; set; }
        public DataDictionary Properties { get; set; }
    }

    public class EnqueuingEventArgs<T> : CancelEventArgs where T : class {
        public IQueue<T> Queue { get; set; }
        public T Data { get; set; }
        public QueueOptions Options { get; set; }
    }

    public class EnqueuedEventArgs<T> : EventArgs where T : class {
        public IQueue<T> Queue { get; set; }
        public IQueueEntry<T> Entry { get; set; }
    }

    public class DequeuedEventArgs<T> : EventArgs where T : class {
        public IQueue<T> Queue { get; set; }
        public IQueueEntry<T> Entry { get; set; }
    }

    public class LockRenewedEventArgs<T> : EventArgs where T : class {
        public IQueue<T> Queue { get; set; }
        public IQueueEntry<T> Entry { get; set; }
    }

    public class CompletedEventArgs<T> : EventArgs where T : class {
        public IQueue<T> Queue { get; set; }
        public IQueueEntry<T> Entry { get; set; }
    }

    public class AbandonedEventArgs<T> : EventArgs where T : class {
        public IQueue<T> Queue { get; set; }
        public IQueueEntry<T> Entry { get; set; }
    }
}
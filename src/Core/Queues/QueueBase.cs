using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Serializer;
using Foundatio.Utility;

namespace Foundatio.Queues {
    public abstract class QueueBase<T> : IQueue<T> where T : class {
        private readonly InMemoryCacheClient _queueEntryCache = new InMemoryCacheClient {
            MaxItems = 1000
        };

        protected readonly ISerializer _serializer;
        protected readonly List<IQueueBehavior<T>> _behaviors = new List<IQueueBehavior<T>>();

        public QueueBase(ISerializer serializer, IEnumerable<IQueueBehavior<T>> behaviors) {
            QueueId = Guid.NewGuid().ToString("N");
            _serializer = serializer ?? new JsonNetSerializer();
            behaviors.ForEach(AttachBehavior);
        }

        public void AttachBehavior(IQueueBehavior<T> behavior) {
            if (behavior != null)
                _behaviors.Add(behavior);
            behavior?.Attach(this);
        }

        public abstract Task<string> EnqueueAsync(T data);

        public abstract Task StartWorkingAsync(Func<QueueEntry<T>, Task> handler, bool autoComplete = false, CancellationToken cancellationToken = default(CancellationToken));

        public abstract Task<QueueEntry<T>> DequeueAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default(CancellationToken));

        public abstract Task CompleteAsync(string id);

        public abstract Task AbandonAsync(string id);

        public abstract Task<IEnumerable<T>> GetDeadletterItemsAsync(CancellationToken cancellationToken = default(CancellationToken));

        public abstract Task<QueueStats> GetQueueStatsAsync();

        public abstract Task DeleteQueueAsync();

        public IReadOnlyCollection<IQueueBehavior<T>> Behaviors => _behaviors;

        public virtual event EventHandler<EnqueuingEventArgs<T>> Enqueuing;

        protected virtual Task<bool> OnEnqueuingAsync(T data) {
            var args = new EnqueuingEventArgs<T> {
                Queue = this,
                Data = data
            };

            Enqueuing?.Invoke(this, args);
            return Task.FromResult(!args.Cancel);
        }

        public virtual event EventHandler<EnqueuedEventArgs<T>> Enqueued;

        protected virtual Task OnEnqueuedAsync(T data, string id) {
            Enqueued?.Invoke(this, new EnqueuedEventArgs<T> {
                Queue = this,
                Data = data,
                Metadata = new QueueEntryMetadata {
                    Attempts = 0,
                    EnqueuedTimeUtc = DateTime.UtcNow,
                    Id = id
                }
            });

            return TaskHelper.Completed();
        }

        public virtual event EventHandler<DequeuedEventArgs<T>> Dequeued;

        protected virtual async Task OnDequeuedAsync(QueueEntry<T> entry) {
            var info = entry.ToMetadata();
            Dequeued?.Invoke(this, new DequeuedEventArgs<T> {
                Queue = this,
                Data = entry.Value,
                Metadata = info
            });

            await _queueEntryCache.SetAsync(entry.Id, info).AnyContext();
        }

        public virtual event EventHandler<CompletedEventArgs<T>> Completed;

        protected virtual async Task OnCompletedAsync(string id) {
            var queueEntry = await _queueEntryCache.GetAsync<QueueEntryMetadata>(id).AnyContext();
            if (queueEntry != null && queueEntry.DequeuedTimeUtc > DateTime.MinValue)
                queueEntry.ProcessingTime = DateTime.UtcNow.Subtract(queueEntry.DequeuedTimeUtc);

            Completed?.Invoke(this, new CompletedEventArgs<T> {
                Queue = this,
                Metadata = queueEntry
            });

            await _queueEntryCache.RemoveAsync(id).AnyContext();
        }

        public virtual event EventHandler<AbandonedEventArgs<T>> Abandoned;

        protected virtual async Task OnAbandonedAsync(string id) {
            var queueEntry = await _queueEntryCache.GetAsync<QueueEntryMetadata>(id).AnyContext();
            if (queueEntry != null && queueEntry.DequeuedTimeUtc > DateTime.MinValue)
                queueEntry.ProcessingTime = DateTime.UtcNow.Subtract(queueEntry.DequeuedTimeUtc);

            Abandoned?.Invoke(this, new AbandonedEventArgs<T> {
                Queue = this,
                Metadata = queueEntry
            });

            await _queueEntryCache.RemoveAsync(id).AnyContext();
        }

        public string QueueId { get; protected set; }

        ISerializer IHaveSerializer.Serializer => _serializer;

        public virtual void Dispose() {
            Logger.Trace().Message("Queue {0} dispose", typeof(T).Name).Write();

            var disposableSerializer = _serializer as IDisposable;
            disposableSerializer?.Dispose();

            _behaviors.OfType<IDisposable>().ForEach(b => b.Dispose());
        }
    }
}
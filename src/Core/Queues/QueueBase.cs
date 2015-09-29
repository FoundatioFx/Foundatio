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
        
        public abstract Task<QueueEntry<T>> DequeueAsync(CancellationToken cancellationToken = default(CancellationToken));

        public abstract Task CompleteAsync(string id);

        public abstract Task AbandonAsync(string id);

        public abstract Task<IEnumerable<T>> GetDeadletterItemsAsync(CancellationToken cancellationToken = default(CancellationToken));

        public abstract Task<QueueStats> GetQueueStatsAsync();

        public abstract Task DeleteQueueAsync();
        
        public abstract void StartWorking(Func<QueueEntry<T>, CancellationToken, Task> handler, bool autoComplete = false, CancellationToken cancellationToken = default(CancellationToken));

        public IReadOnlyCollection<IQueueBehavior<T>> Behaviors => _behaviors;

        public AsyncEvent<EnqueuingEventArgs<T>> Enqueuing { get; set; } = new AsyncEvent<EnqueuingEventArgs<T>>();

        protected virtual async Task<bool> OnEnqueuingAsync(T data) {
            var args = new EnqueuingEventArgs<T> {
                Queue = this,
                Data = data
            };
            
            await (Enqueuing?.InvokeAsync(this, args) ?? TaskHelper.Completed()).AnyContext();
            return !args.Cancel;
        }

        public AsyncEvent<EnqueuedEventArgs<T>> Enqueued { get; set; } = new AsyncEvent<EnqueuedEventArgs<T>>(true);

        protected virtual async Task OnEnqueuedAsync(T data, string id) {
            await (Enqueued?.InvokeAsync(this, new EnqueuedEventArgs<T> {
                Queue = this,
                Data = data,
                Metadata = new QueueEntryMetadata {
                    Attempts = 0,
                    EnqueuedTimeUtc = DateTime.UtcNow,
                    Id = id
                }
            }) ?? TaskHelper.Completed()).AnyContext();
        }

        public AsyncEvent<DequeuedEventArgs<T>> Dequeued { get; set; } = new AsyncEvent<DequeuedEventArgs<T>>(true);

        protected virtual async Task OnDequeuedAsync(QueueEntry<T> entry) {
            var info = entry.ToMetadata();
            await (Dequeued?.InvokeAsync(this, new DequeuedEventArgs<T> {
                Queue = this,
                Data = entry.Value,
                Metadata = info
            }) ?? TaskHelper.Completed()).AnyContext();

            await _queueEntryCache.SetAsync(entry.Id, info).AnyContext();
        }

        public AsyncEvent<CompletedEventArgs<T>> Completed { get; set; } = new AsyncEvent<CompletedEventArgs<T>>(true);

        protected virtual async Task OnCompletedAsync(string id) {
            var queueEntry = await _queueEntryCache.GetAsync<QueueEntryMetadata>(id).AnyContext();
            if (queueEntry.HasValue && queueEntry.Value.DequeuedTimeUtc > DateTime.MinValue)
                queueEntry.Value.ProcessingTime = DateTime.UtcNow.Subtract(queueEntry.Value.DequeuedTimeUtc);

            await (Completed?.InvokeAsync(this, new CompletedEventArgs<T> {
                Queue = this,
                Metadata = queueEntry.Value
            }) ?? TaskHelper.Completed()).AnyContext();

            await _queueEntryCache.RemoveAsync(id).AnyContext();
        }

        public AsyncEvent<AbandonedEventArgs<T>> Abandoned { get; set; } = new AsyncEvent<AbandonedEventArgs<T>>(true);

        protected virtual async Task OnAbandonedAsync(string id) {
            var queueEntry = await _queueEntryCache.GetAsync<QueueEntryMetadata>(id).AnyContext();
            if (queueEntry.HasValue && queueEntry.Value.DequeuedTimeUtc > DateTime.MinValue)
                queueEntry.Value.ProcessingTime = DateTime.UtcNow.Subtract(queueEntry.Value.DequeuedTimeUtc);
            
            await (Abandoned?.InvokeAsync(this, new AbandonedEventArgs<T> {
                Queue = this,
                Metadata = queueEntry.Value
            }) ?? TaskHelper.Completed()).AnyContext();

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
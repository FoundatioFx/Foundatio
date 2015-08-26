using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Serializer;

namespace Foundatio.Queues {
    public abstract class QueueBase<T> : IQueue<T> where T : class {
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

        public abstract Task StartWorkingAsync(Action<QueueEntry<T>> handler, bool autoComplete = false, CancellationToken cancellationToken = default(CancellationToken));

        public abstract Task StopWorkingAsync();

        public abstract Task<QueueEntry<T>> DequeueAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default(CancellationToken));

        public abstract Task CompleteAsync(IQueueEntryMetadata entry);

        public abstract Task AbandonAsync(IQueueEntryMetadata entry);

        public abstract Task<IEnumerable<T>> GetDeadletterItemsAsync(CancellationToken cancellationToken = default(CancellationToken));
        
        public abstract Task<QueueStats> GetQueueStatsAsync();

        public IReadOnlyCollection<IQueueBehavior<T>> Behaviors => _behaviors;

        public virtual event EventHandler<EnqueuingEventArgs<T>> Enqueuing;

        protected virtual bool OnEnqueuing(T data) {
            var args = new EnqueuingEventArgs<T> {
                Queue = this,
                Data = data
            };
            Enqueuing?.Invoke(this, args);
            return !args.Cancel;
        }

        public virtual event EventHandler<EnqueuedEventArgs<T>> Enqueued;

        protected virtual void OnEnqueued(T data, string id) {
            Enqueued?.Invoke(this, new EnqueuedEventArgs<T> {
                Queue = this,
                Data = data,
                Id = id
            });
        }

        public virtual event EventHandler<DequeuedEventArgs<T>> Dequeued;

        protected virtual void OnDequeued(QueueEntry<T> entry) {
            Dequeued?.Invoke(this, new DequeuedEventArgs<T> {
                Queue = this,
                Data = entry.Value,
                Metadata = entry
            });
        }

        public virtual event EventHandler<CompletedEventArgs<T>> Completed;

        protected virtual void OnCompleted(IQueueEntryMetadata entry) {
            Completed?.Invoke(this, new CompletedEventArgs<T> {
                Queue = this,
                Metadata = entry
            });
        }

        public virtual event EventHandler<AbandonedEventArgs<T>> Abandoned;

        protected virtual void OnAbandoned(IQueueEntryMetadata entry) {
            Abandoned?.Invoke(this, new AbandonedEventArgs<T> {
                Queue = this,
                Metadata = entry
            });
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
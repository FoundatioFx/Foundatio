using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Foundatio.Caching;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Serializer;

namespace Foundatio.Queues
{
    public abstract class QueueBase<T> : IQueue<T> where T : class
    {
        private readonly InMemoryCacheClient _queueEntryCache = new InMemoryCacheClient { MaxItems = 1000 };
        protected ISerializer _serializer;
        protected List<IQueueBehavior<T>> _behaviours = new List<IQueueBehavior<T>>();

        public QueueBase(ISerializer serializer, IEnumerable<IQueueBehavior<T>> behaviours)
        {
            QueueId = Guid.NewGuid().ToString("N");
            _serializer = serializer ?? new JsonNetSerializer();
            behaviours.ForEach(AttachBehavior);
        }

        public void AttachBehavior(IQueueBehavior<T> behavior)
        {
            if (behavior != null)
                _behaviours.Add(behavior);
            behavior?.Attach(this);
        }

        public abstract string Enqueue(T data);
        public abstract void StartWorking(Action<QueueEntry<T>> handler, bool autoComplete = false, CancellationToken token = default(CancellationToken));
        public abstract QueueEntry<T> Dequeue(TimeSpan? timeout = null, CancellationToken cancellationToken = new CancellationToken());
        public abstract void Complete(string id);
        public abstract void Abandon(string id);
        public abstract IEnumerable<T> GetDeadletterItems();
        public abstract void DeleteQueue();
        public abstract QueueStats GetQueueStats();

        public IReadOnlyCollection<IQueueBehavior<T>> Behaviours => _behaviours;

        public virtual event EventHandler<EnqueuingEventArgs<T>> Enqueuing;
        protected virtual bool OnEnqueuing(T data)
        {
            var args = new EnqueuingEventArgs<T> { Queue = this, Data = data };
            Enqueuing?.Invoke(this, args);
            return !args.Cancel;
        }

        public virtual event EventHandler<EnqueuedEventArgs<T>> Enqueued;
        protected virtual void OnEnqueued(T data, string id)
        {
            Enqueued?.Invoke(this, new EnqueuedEventArgs<T> { Queue = this, Data = data, Metadata = new QueueEntryMetadata { Attempts = 0, EnqueuedTimeUtc = DateTime.UtcNow, Id = id } });
        }

        public virtual event EventHandler<DequeuedEventArgs<T>> Dequeued;
        protected virtual void OnDequeued(QueueEntry<T> entry)
        {
            var info = entry.ToMetadata();
            Dequeued?.Invoke(this, new DequeuedEventArgs<T> { Queue = this, Data = entry.Value, Metadata = info });
            _queueEntryCache.Set(entry.Id, info);
        }

        public virtual event EventHandler<CompletedEventArgs<T>> Completed;
        protected virtual void OnCompleted(string id)
        {
            var queueEntry = _queueEntryCache.Get<QueueEntryMetadata>(id);
            queueEntry.ProcessingTime = DateTime.UtcNow.Subtract(queueEntry.DequeuedTimeUtc);
            Completed?.Invoke(this, new CompletedEventArgs<T> { Queue = this, Metadata = queueEntry });
            _queueEntryCache.Remove(id);
        }

        public virtual event EventHandler<AbandonedEventArgs<T>> Abandoned;
        protected virtual void OnAbandoned(string id)
        {
            var queueEntry = _queueEntryCache.Get<QueueEntryMetadata>(id);
            queueEntry.ProcessingTime = DateTime.UtcNow.Subtract(queueEntry.DequeuedTimeUtc);
            Abandoned?.Invoke(this, new AbandonedEventArgs<T> { Queue = this, Metadata = queueEntry });
            _queueEntryCache.Remove(id);
        }

        public string QueueId { get; protected set; }

        ISerializer IHaveSerializer.Serializer => _serializer;

        public virtual void Dispose()
        {
            Logger.Trace().Message("Queue {0} dispose", typeof(T).Name).Write();

            var disposableSerializer = _serializer as IDisposable;
            disposableSerializer?.Dispose();

            _behaviours.OfType<IDisposable>().ForEach(b => b.Dispose());
        }
    }
}
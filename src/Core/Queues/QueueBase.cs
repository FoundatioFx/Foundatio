using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Serializer;
using Foundatio.Utility;
using Nito.AsyncEx.Synchronous;

namespace Foundatio.Queues {
    public abstract class QueueBase<T> : MaintenanceBase, IQueue<T> where T : class {
        protected readonly ISerializer _serializer;
        protected readonly List<IQueueBehavior<T>> _behaviors = new List<IQueueBehavior<T>>();

        protected QueueBase(ISerializer serializer, IEnumerable<IQueueBehavior<T>> behaviors, ILoggerFactory loggerFactory = null) : base(loggerFactory) {
            QueueId = Guid.NewGuid().ToString("N");
            _serializer = serializer ?? new JsonNetSerializer();
            behaviors.ForEach(AttachBehavior);
        }

        public void AttachBehavior(IQueueBehavior<T> behavior) {
            if (behavior != null) {
                _behaviors.Add(behavior);
                behavior.Attach(this);
            }
        }

        protected abstract Task EnsureQueueCreatedAsync(CancellationToken cancellationToken = default(CancellationToken));

        protected abstract Task<string> EnqueueImplAsync(T data);
        public async Task<string> EnqueueAsync(T data) {
            await EnsureQueueCreatedAsync().AnyContext();
            return await EnqueueImplAsync(data).AnyContext();
        }

        protected abstract Task<IQueueEntry<T>> DequeueImplAsync(CancellationToken cancellationToken);
        public async Task<IQueueEntry<T>> DequeueAsync(CancellationToken cancellationToken) {
            await EnsureQueueCreatedAsync(cancellationToken).AnyContext();
            return await DequeueImplAsync(cancellationToken).AnyContext();
        }
        public virtual Task<IQueueEntry<T>> DequeueAsync(TimeSpan? timeout = null)
            => this.DequeueAsync(timeout.GetValueOrDefault(TimeSpan.FromSeconds(30)).ToCancellationToken());

        public abstract Task RenewLockAsync(IQueueEntry<T> queueEntry);

        public abstract Task CompleteAsync(IQueueEntry<T> queueEntry);

        public abstract Task AbandonAsync(IQueueEntry<T> queueEntry);

        protected abstract Task<IEnumerable<T>> GetDeadletterItemsImplAsync(CancellationToken cancellationToken);
        public async Task<IEnumerable<T>> GetDeadletterItemsAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            await EnsureQueueCreatedAsync(cancellationToken);
            return await GetDeadletterItemsImplAsync(cancellationToken);
        }

        protected abstract Task<QueueStats> GetQueueStatsImplAsync();
        public async Task<QueueStats> GetQueueStatsAsync() {
            await EnsureQueueCreatedAsync().AnyContext();
            return await GetQueueStatsImplAsync().AnyContext();
        }

        public abstract Task DeleteQueueAsync();
        
        protected abstract void StartWorkingImpl(Func<IQueueEntry<T>, CancellationToken, Task> handler, bool autoComplete, CancellationToken cancellationToken);
        public async Task StartWorkingAsync(Func<IQueueEntry<T>, CancellationToken, Task> handler, bool autoComplete = false, CancellationToken cancellationToken = default(CancellationToken)) {
            await EnsureQueueCreatedAsync(cancellationToken).AnyContext();
            StartWorkingImpl(handler, autoComplete, cancellationToken);
        }

        public IReadOnlyCollection<IQueueBehavior<T>> Behaviors => _behaviors;

        public AsyncEvent<EnqueuingEventArgs<T>> Enqueuing { get; } = new AsyncEvent<EnqueuingEventArgs<T>>();

        protected virtual async Task<bool> OnEnqueuingAsync(T data) {
            var args = new EnqueuingEventArgs<T> {
                Queue = this,
                Data = data
            };
            
            await (Enqueuing?.InvokeAsync(this, args) ?? TaskHelper.Completed).AnyContext();
            return !args.Cancel;
        }

        public AsyncEvent<EnqueuedEventArgs<T>> Enqueued { get; } = new AsyncEvent<EnqueuedEventArgs<T>>(true);

        protected virtual async Task OnEnqueuedAsync(IQueueEntry<T> entry) {
            await (Enqueued?.InvokeAsync(this, new EnqueuedEventArgs<T> {
                Queue = this,
                Entry = entry
            }) ?? TaskHelper.Completed).AnyContext();
        }

        public AsyncEvent<DequeuedEventArgs<T>> Dequeued { get; } = new AsyncEvent<DequeuedEventArgs<T>>(true);

        protected virtual async Task OnDequeuedAsync(IQueueEntry<T> entry) {
            await (Dequeued?.InvokeAsync(this, new DequeuedEventArgs<T> {
                Queue = this,
                Entry = entry
            }) ?? TaskHelper.Completed).AnyContext();
        }

        public AsyncEvent<LockRenewedEventArgs<T>> LockRenewed { get; } = new AsyncEvent<LockRenewedEventArgs<T>>(true);

        protected virtual async Task OnLockRenewedAsync(IQueueEntry<T> entry) {
            await (LockRenewed?.InvokeAsync(this, new LockRenewedEventArgs<T> {
                Queue = this,
                Entry = entry
            }) ?? TaskHelper.Completed).AnyContext();
        }

        public AsyncEvent<CompletedEventArgs<T>> Completed { get; } = new AsyncEvent<CompletedEventArgs<T>>(true);
        
        protected virtual async Task OnCompletedAsync(IQueueEntry<T> entry) {
            var metadata = entry as QueueEntry<T>;
            if (metadata != null && metadata.DequeuedTimeUtc > DateTime.MinValue)
                metadata.ProcessingTime = DateTime.UtcNow.Subtract(metadata.DequeuedTimeUtc);

            await (Completed?.InvokeAsync(this, new CompletedEventArgs<T> {
                Queue = this,
                Entry = entry
            }) ?? TaskHelper.Completed).AnyContext();
        }

        public AsyncEvent<AbandonedEventArgs<T>> Abandoned { get; } = new AsyncEvent<AbandonedEventArgs<T>>(true);

        protected virtual async Task OnAbandonedAsync(IQueueEntry<T> entry) {
            var metadata = entry as QueueEntry<T>;
            if (metadata != null && metadata.DequeuedTimeUtc > DateTime.MinValue)
                metadata.ProcessingTime = DateTime.UtcNow.Subtract(metadata.DequeuedTimeUtc);
            
            await (Abandoned?.InvokeAsync(this, new AbandonedEventArgs<T> {
                Queue = this,
                Entry = entry
            }) ?? TaskHelper.Completed).AnyContext();
        }

        public string QueueId { get; protected set; }

        ISerializer IHaveSerializer.Serializer => _serializer;

        public override void Dispose() {
            _logger.Trace("Queue {0} dispose", typeof(T).Name);

            base.Dispose();

            // ReSharper disable once SuspiciousTypeConversion.Global
            var disposableSerializer = _serializer as IDisposable;
            disposableSerializer?.Dispose();

            _behaviors.OfType<IDisposable>().ForEach(b => b.Dispose());
        }
    }
}
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Queues {
    public abstract class QueueBase<T, TOptions> : MaintenanceBase, IQueue<T> where T : class where TOptions : QueueOptionsBase<T> {
        protected readonly TOptions _options;
        protected readonly ISerializer _serializer;
        protected readonly List<IQueueBehavior<T>> _behaviors = new List<IQueueBehavior<T>>();
        protected readonly CancellationTokenSource _queueDisposedCancellationTokenSource;

        protected QueueBase(TOptions options) : base(options?.LoggerFactory) {
            _options = options ?? throw new ArgumentNullException(nameof(options));

            QueueId = options.Name + Guid.NewGuid().ToString("N").Substring(10);
            _serializer = options.Serializer ?? new JsonNetSerializer();
            options.Behaviors.ForEach(AttachBehavior);

            _queueDisposedCancellationTokenSource = new CancellationTokenSource();
        }

        public void AttachBehavior(IQueueBehavior<T> behavior) {
            if (behavior == null)
                return;

            _behaviors.Add(behavior);
            behavior.Attach(this);
        }

        protected abstract Task EnsureQueueCreatedAsync(CancellationToken cancellationToken = default(CancellationToken));

        protected abstract Task<string> EnqueueImplAsync(T data);
        public async Task<string> EnqueueAsync(T data) {
            await EnsureQueueCreatedAsync().AnyContext();
            return await EnqueueImplAsync(data).AnyContext();
        }

        protected abstract Task<IQueueEntry<T>> DequeueImplAsync(CancellationToken linkedCancellationToken);
        public async Task<IQueueEntry<T>> DequeueAsync(CancellationToken cancellationToken) {
            var linkedCancellationToken = GetLinkedDisposableCanncellationToken(cancellationToken);
            await EnsureQueueCreatedAsync(linkedCancellationToken).AnyContext();
            return await DequeueImplAsync(linkedCancellationToken).AnyContext();
        }
        public virtual Task<IQueueEntry<T>> DequeueAsync(TimeSpan? timeout = null)
            => DequeueAsync(timeout.GetValueOrDefault(TimeSpan.FromSeconds(30)).ToCancellationToken());

        public abstract Task RenewLockAsync(IQueueEntry<T> queueEntry);

        public abstract Task CompleteAsync(IQueueEntry<T> queueEntry);

        public abstract Task AbandonAsync(IQueueEntry<T> queueEntry);

        protected abstract Task<IEnumerable<T>> GetDeadletterItemsImplAsync(CancellationToken cancellationToken);
        public async Task<IEnumerable<T>> GetDeadletterItemsAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            await EnsureQueueCreatedAsync(cancellationToken).AnyContext();
            return await GetDeadletterItemsImplAsync(cancellationToken).AnyContext();
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
            var enqueueing = Enqueuing;
            if (enqueueing == null)
                return false;

            var args = new EnqueuingEventArgs<T> { Queue = this, Data = data };
            await enqueueing.InvokeAsync(this, args).AnyContext();
            return !args.Cancel;
        }

        public AsyncEvent<EnqueuedEventArgs<T>> Enqueued { get; } = new AsyncEvent<EnqueuedEventArgs<T>>(true);

        protected virtual Task OnEnqueuedAsync(IQueueEntry<T> entry) {
            var enqueued = Enqueued;
            if (enqueued == null)
                return Task.CompletedTask;

            var args = new EnqueuedEventArgs<T> { Queue = this, Entry = entry };
            return enqueued.InvokeAsync(this, args);
        }

        public AsyncEvent<DequeuedEventArgs<T>> Dequeued { get; } = new AsyncEvent<DequeuedEventArgs<T>>(true);

        protected virtual Task OnDequeuedAsync(IQueueEntry<T> entry) {
            var dequeued = Dequeued;
            if (dequeued == null)
                return Task.CompletedTask;

            var args = new DequeuedEventArgs<T> { Queue = this, Entry = entry };
            return dequeued.InvokeAsync(this, args);
        }

        public AsyncEvent<LockRenewedEventArgs<T>> LockRenewed { get; } = new AsyncEvent<LockRenewedEventArgs<T>>(true);

        protected virtual Task OnLockRenewedAsync(IQueueEntry<T> entry) {
            var lockRenewed = LockRenewed;
            if (lockRenewed == null)
                return Task.CompletedTask;

            var args = new LockRenewedEventArgs<T> { Queue = this, Entry = entry };
            return lockRenewed.InvokeAsync(this, args);
        }

        public AsyncEvent<CompletedEventArgs<T>> Completed { get; } = new AsyncEvent<CompletedEventArgs<T>>(true);

        protected virtual Task OnCompletedAsync(IQueueEntry<T> entry) {
            if (entry is QueueEntry<T> metadata && metadata.DequeuedTimeUtc > DateTime.MinValue)
                metadata.ProcessingTime = SystemClock.UtcNow.Subtract(metadata.DequeuedTimeUtc);

            var completed = Completed;
            if (completed == null)
                return Task.CompletedTask;

            var args = new CompletedEventArgs<T> { Queue = this, Entry = entry };
            return completed.InvokeAsync(this, args);
        }

        public AsyncEvent<AbandonedEventArgs<T>> Abandoned { get; } = new AsyncEvent<AbandonedEventArgs<T>>(true);

        protected virtual Task OnAbandonedAsync(IQueueEntry<T> entry) {
            if (entry is QueueEntry<T> metadata && metadata.DequeuedTimeUtc > DateTime.MinValue)
                metadata.ProcessingTime = SystemClock.UtcNow.Subtract(metadata.DequeuedTimeUtc);

            var abandoned = Abandoned;
            if (abandoned == null)
                return Task.CompletedTask;

            var args = new AbandonedEventArgs<T> { Queue = this, Entry = entry };
            return abandoned.InvokeAsync(this, args);
        }

        public string QueueId { get; protected set; }

        ISerializer IHaveSerializer.Serializer => _serializer;

        protected CancellationToken GetLinkedDisposableCanncellationToken(CancellationToken cancellationToken) {
            if (cancellationToken.IsCancellationRequested)
                return cancellationToken;

            return CancellationTokenSource.CreateLinkedTokenSource(_queueDisposedCancellationTokenSource.Token, cancellationToken).Token;
        }

        protected CancellationToken GetDequeueCanncellationToken(CancellationToken linkedDisposedCancellationToken) {
            if (linkedDisposedCancellationToken.IsCancellationRequested)
                return linkedDisposedCancellationToken;

            return CancellationTokenSource.CreateLinkedTokenSource(linkedDisposedCancellationToken, new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token).Token;
        }

        public override void Dispose() {
            _logger.LogTrace("Queue {0} dispose", _options.Name);
            _queueDisposedCancellationTokenSource?.Cancel();
            base.Dispose();

            Abandoned?.Dispose();
            Completed?.Dispose();
            Dequeued?.Dispose();
            Enqueued?.Dispose();
            Enqueuing?.Dispose();
            LockRenewed?.Dispose();

            foreach (var behavior in _behaviors.OfType<IDisposable>())
                behavior.Dispose();

            _behaviors.Clear();
        }
    }
}
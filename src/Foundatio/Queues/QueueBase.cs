using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Queues {
    public abstract class QueueBase<T, TOptions> : MaintenanceBase, IQueue<T>, IQueueActivity where T : class where TOptions : SharedQueueOptions<T> {
        protected readonly TOptions _options;
        protected readonly ISerializer _serializer;
        private readonly List<IQueueBehavior<T>> _behaviors = new();
        protected readonly CancellationTokenSource _queueDisposedCancellationTokenSource;
        private bool _isDisposed;

        protected QueueBase(TOptions options) : base(options?.LoggerFactory) {
            _options = options ?? throw new ArgumentNullException(nameof(options));

            QueueId = options.Name + Guid.NewGuid().ToString("N").Substring(10);
            _serializer = options.Serializer ?? DefaultSerializer.Instance;
            options.Behaviors.ForEach(AttachBehavior);

            _queueDisposedCancellationTokenSource = new CancellationTokenSource();
        }

        public void AttachBehavior(IQueueBehavior<T> behavior) {
            if (behavior == null)
                return;

            _behaviors.Add(behavior);
            behavior.Attach(this);
        }

        protected abstract Task EnsureQueueCreatedAsync(CancellationToken cancellationToken = default);

        protected abstract Task<string> EnqueueImplAsync(T data, QueueEntryOptions options);
        public async Task<string> EnqueueAsync(T data, QueueEntryOptions options = null) {
            await EnsureQueueCreatedAsync().AnyContext();
            
            LastEnqueueActivity = SystemClock.UtcNow;
            options ??= new QueueEntryOptions();
            
            return await EnqueueImplAsync(data, options).AnyContext();
        }

        protected abstract Task<IQueueEntry<T>> DequeueImplAsync(CancellationToken linkedCancellationToken);
        public async Task<IQueueEntry<T>> DequeueAsync(CancellationToken cancellationToken) {
            using var linkedCancellationToken = GetLinkedDisposableCancellationTokenSource(cancellationToken);
            await EnsureQueueCreatedAsync(linkedCancellationToken.Token).AnyContext();

            LastDequeueActivity = SystemClock.UtcNow;
            return await DequeueImplAsync(linkedCancellationToken.Token).AnyContext();
        }

        public virtual async Task<IQueueEntry<T>> DequeueAsync(TimeSpan? timeout = null) {
            using var timeoutCancellationTokenSource = timeout.ToCancellationTokenSource(TimeSpan.FromSeconds(30));
            return await DequeueAsync(timeoutCancellationTokenSource.Token).AnyContext();
        }

        public abstract Task RenewLockAsync(IQueueEntry<T> queueEntry);

        public abstract Task CompleteAsync(IQueueEntry<T> queueEntry);

        public abstract Task AbandonAsync(IQueueEntry<T> queueEntry);

        protected abstract Task<IEnumerable<T>> GetDeadletterItemsImplAsync(CancellationToken cancellationToken);
        public async Task<IEnumerable<T>> GetDeadletterItemsAsync(CancellationToken cancellationToken = default) {
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
        public async Task StartWorkingAsync(Func<IQueueEntry<T>, CancellationToken, Task> handler, bool autoComplete = false, CancellationToken cancellationToken = default) {
            await EnsureQueueCreatedAsync(cancellationToken).AnyContext();
            StartWorkingImpl(handler, autoComplete, cancellationToken);
        }

        public IReadOnlyCollection<IQueueBehavior<T>> Behaviors => _behaviors;

        public AsyncEvent<EnqueuingEventArgs<T>> Enqueuing { get; } = new AsyncEvent<EnqueuingEventArgs<T>>();
        
        protected virtual async Task<bool> OnEnqueuingAsync(T data, QueueEntryOptions options) {
            if (String.IsNullOrEmpty(options.CorrelationId))
                options.CorrelationId = Activity.Current?.Id;

            var enqueueing = Enqueuing;
            if (enqueueing == null)
                return false;

            var args = new EnqueuingEventArgs<T> { Queue = this, Data = data, Options = options };
            await enqueueing.InvokeAsync(this, args).AnyContext();

            return !args.Cancel;
        }

        public AsyncEvent<EnqueuedEventArgs<T>> Enqueued { get; } = new AsyncEvent<EnqueuedEventArgs<T>>(true);

        protected virtual Task OnEnqueuedAsync(IQueueEntry<T> entry) {
            LastEnqueueActivity = SystemClock.UtcNow;
            
            var enqueued = Enqueued;
            if (enqueued == null)
                return Task.CompletedTask;

            var args = new EnqueuedEventArgs<T> { Queue = this, Entry = entry };
            return enqueued.InvokeAsync(this, args);
        }

        public AsyncEvent<DequeuedEventArgs<T>> Dequeued { get; } = new AsyncEvent<DequeuedEventArgs<T>>(true);

        protected virtual void StartProcessQueueEntryActivity(IQueueEntry<T> entry) {
            var tags = new Dictionary<string, object> {
                { "Id", entry.Id },
                { "QueueEntry", entry },
                { "CorrelationId", entry.CorrelationId }
            };

            var activity = FoundatioDiagnostics.ActivitySource.StartActivity("ProcessQueueEntry", ActivityKind.Internal, null, tags);
            if (activity == null)
                return;

            // TODO: In 6.0, we will be able to delay activity creation and set display name before starting the activity
            activity.DisplayName = $"Queue: {entry.EntryType.Name}";
            if (entry.GetValue() is WorkItemData workItem && !String.IsNullOrEmpty(workItem.SubMetricName))
                activity.DisplayName = $"Queue Work Item: {workItem.SubMetricName}";

            EnrichProcessQueueEntryActivity(activity, entry);

            entry.Properties["@Activity"] = activity;
        }

        protected virtual void EnrichProcessQueueEntryActivity(Activity activity, IQueueEntry<T> entry) {}

        protected virtual void StopProcessQueueEntryActivity(IQueueEntry<T> entry) {
            if (!entry.Properties.TryGetValue("@Activity", out object a) || a is not Activity activity)
                return;

            entry.Properties.Remove("@Activity");
            activity.Stop();
        }

        protected virtual Task OnDequeuedAsync(IQueueEntry<T> entry) {
            LastDequeueActivity = SystemClock.UtcNow;

            StartProcessQueueEntryActivity(entry);

            var dequeued = Dequeued;
            if (dequeued == null)
                return Task.CompletedTask;

            var args = new DequeuedEventArgs<T> { Queue = this, Entry = entry };
            return dequeued.InvokeAsync(this, args);
        }

        public AsyncEvent<LockRenewedEventArgs<T>> LockRenewed { get; } = new AsyncEvent<LockRenewedEventArgs<T>>(true);

        protected virtual Task OnLockRenewedAsync(IQueueEntry<T> entry) {
            LastDequeueActivity = SystemClock.UtcNow;
            
            var lockRenewed = LockRenewed;
            if (lockRenewed == null)
                return Task.CompletedTask;

            var args = new LockRenewedEventArgs<T> { Queue = this, Entry = entry };
            return lockRenewed.InvokeAsync(this, args);
        }

        public AsyncEvent<CompletedEventArgs<T>> Completed { get; } = new AsyncEvent<CompletedEventArgs<T>>(true);

        protected virtual async Task OnCompletedAsync(IQueueEntry<T> entry) {
            var now = SystemClock.UtcNow;
            LastDequeueActivity = now;
            
            if (entry is QueueEntry<T> metadata) {
                if (metadata.EnqueuedTimeUtc > DateTime.MinValue)
                    metadata.TotalTime = now.Subtract(metadata.EnqueuedTimeUtc);

                if (metadata.DequeuedTimeUtc > DateTime.MinValue)
                    metadata.ProcessingTime = now.Subtract(metadata.DequeuedTimeUtc);
            }

            if (Completed != null) {
                var args = new CompletedEventArgs<T> { Queue = this, Entry = entry };
                await Completed.InvokeAsync(this, args).AnyContext();
            }

            StopProcessQueueEntryActivity(entry);
        }

        public AsyncEvent<AbandonedEventArgs<T>> Abandoned { get; } = new AsyncEvent<AbandonedEventArgs<T>>(true);

        protected virtual async Task OnAbandonedAsync(IQueueEntry<T> entry) {
            LastDequeueActivity = SystemClock.UtcNow;
            
            if (entry is QueueEntry<T> metadata && metadata.DequeuedTimeUtc > DateTime.MinValue)
                metadata.ProcessingTime = SystemClock.UtcNow.Subtract(metadata.DequeuedTimeUtc);

            if (Abandoned != null) {
                var args = new AbandonedEventArgs<T> { Queue = this, Entry = entry };
                await Abandoned.InvokeAsync(this, args).AnyContext();
            }

            StopProcessQueueEntryActivity(entry);
        }

        public string QueueId { get; protected set; }

        public DateTime? LastEnqueueActivity { get; protected set; }
        
        public DateTime? LastDequeueActivity { get; protected set; }

        ISerializer IHaveSerializer.Serializer => _serializer;

        protected CancellationTokenSource GetLinkedDisposableCancellationTokenSource(CancellationToken cancellationToken) {
            return CancellationTokenSource.CreateLinkedTokenSource(_queueDisposedCancellationTokenSource.Token, cancellationToken);
        }

        public override void Dispose() {
            if (_isDisposed) {
                _logger.LogTrace("Queue {Name} ({Id})  dispose was already called.", _options.Name, QueueId);
                return;
            }
            
            _isDisposed = true;
            _logger.LogTrace("Queue {Name} ({Id}) dispose", _options.Name, QueueId);
            _queueDisposedCancellationTokenSource?.Cancel();
            _queueDisposedCancellationTokenSource?.Dispose();
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
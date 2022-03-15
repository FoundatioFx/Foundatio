using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Metrics;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Queues {
    public abstract class QueueBase<T, TOptions> : MaintenanceBase, IQueue<T>, IQueueActivity where T : class where TOptions : SharedQueueOptions<T> {
        protected readonly TOptions _options;
        private readonly string _metricsPrefix;
        protected readonly ISerializer _serializer;
        private ScheduledTimer _timer;

        private readonly Counter<int> _enqueuedCounter;
        private readonly Counter<int> _dequeuedCounter;
        private readonly Histogram<int> _queueTimeHistogram;
        private readonly Counter<int> _completedCounter;
        private readonly Histogram<int> _processTimeHistogram;
        private readonly Histogram<int> _totalTimeHistogram;
        private readonly Counter<int> _abandonedCounter;
        private readonly ObservableGauge<long> _countGauge;
        private readonly ObservableGauge<long> _workingGauge;
        private readonly ObservableGauge<long> _deadletterGauge;

        private readonly List<IQueueBehavior<T>> _behaviors = new();
        protected readonly CancellationTokenSource _queueDisposedCancellationTokenSource;
        private bool _isDisposed;

        protected QueueBase(TOptions options) : base(options?.LoggerFactory) {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _metricsPrefix = GetMetricsPrefix();

            QueueId = options.Name + Guid.NewGuid().ToString("N").Substring(10);

            _serializer = options.Serializer ?? DefaultSerializer.Instance;
            options.Behaviors.ForEach(AttachBehavior);

            _queueDisposedCancellationTokenSource = new CancellationTokenSource();

            // setup meters
            _enqueuedCounter = FoundatioDiagnostics.Meter.CreateCounter<int>(GetFullMetricName("enqueued"), description: "Number of enqueued items");
            _dequeuedCounter = FoundatioDiagnostics.Meter.CreateCounter<int>(GetFullMetricName("dequeued"), description: "Number of dequeued items");
            _queueTimeHistogram = FoundatioDiagnostics.Meter.CreateHistogram<int>(GetFullMetricName("queuetime"), description: "Time in queue", unit: "ms");
            _completedCounter = FoundatioDiagnostics.Meter.CreateCounter<int>(GetFullMetricName("completed"), description: "Number of completed items");
            _processTimeHistogram = FoundatioDiagnostics.Meter.CreateHistogram<int>(GetFullMetricName("processtime"), description: "Time to process items", unit: "ms");
            _totalTimeHistogram = FoundatioDiagnostics.Meter.CreateHistogram<int>(GetFullMetricName("totaltime"), description: "Total time in queue", unit: "ms");
            _abandonedCounter = FoundatioDiagnostics.Meter.CreateCounter<int>(GetFullMetricName("abandoned"), description: "Number of abandoned items");
            
            _countGauge = FoundatioDiagnostics.Meter.CreateObservableGauge(GetFullMetricName("count"), GetQueueCount, description: "Number of items in the queue");
            _workingGauge = FoundatioDiagnostics.Meter.CreateObservableGauge(GetFullMetricName("working"), GetWorkingCount, description: "Number of items currently being processed");
            _deadletterGauge = FoundatioDiagnostics.Meter.CreateObservableGauge(GetFullMetricName("deadletter"), GetDeadletterCount, description: "Number of items in the deadletter queue");
        }

        public string QueueId { get; protected set; }
        public DateTime? LastEnqueueActivity { get; protected set; }
        public DateTime? LastDequeueActivity { get; protected set; }
        ISerializer IHaveSerializer.Serializer => _serializer;

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
            if (String.IsNullOrEmpty(options.CorrelationId)) {
                options.CorrelationId = Activity.Current?.Id;
                if (!String.IsNullOrEmpty(Activity.Current?.TraceStateString))
                    options.Properties.Add("TraceState", Activity.Current.TraceStateString);
            }

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

            _enqueuedCounter.Add(1);
            IncrementSubCounter(entry.Value, "enqueued");

            var enqueued = Enqueued;
            if (enqueued == null)
                return Task.CompletedTask;

            var args = new EnqueuedEventArgs<T> { Queue = this, Entry = entry };
            return enqueued.InvokeAsync(this, args);
        }

        public AsyncEvent<DequeuedEventArgs<T>> Dequeued { get; } = new AsyncEvent<DequeuedEventArgs<T>>(true);

        protected virtual Task OnDequeuedAsync(IQueueEntry<T> entry) {
            LastDequeueActivity = SystemClock.UtcNow;

            _dequeuedCounter.Add(1);
            IncrementSubCounter(entry.Value, "dequeued");

            var metadata = entry as IQueueEntryMetadata;
            if (metadata != null && (metadata.EnqueuedTimeUtc != DateTime.MinValue || metadata.DequeuedTimeUtc != DateTime.MinValue)) {
                var start = metadata.EnqueuedTimeUtc;
                var end = metadata.DequeuedTimeUtc;
                int time = (int)(end - start).TotalMilliseconds;

                _queueTimeHistogram.Record(time);
                RecordSubHistogram(entry.Value, "queuetime", time);
            }

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

            _completedCounter.Add(1);
            IncrementSubCounter(entry.Value, "completed");

            if (entry is QueueEntry<T> metadata) {
                if (metadata.EnqueuedTimeUtc > DateTime.MinValue) {
                    metadata.TotalTime = now.Subtract(metadata.EnqueuedTimeUtc);
                    _totalTimeHistogram.Record((int)metadata.TotalTime.TotalMilliseconds);
                    RecordSubHistogram(entry.Value, "totaltime", (int)metadata.TotalTime.TotalMilliseconds);
                }

                if (metadata.DequeuedTimeUtc > DateTime.MinValue) {
                    metadata.ProcessingTime = now.Subtract(metadata.DequeuedTimeUtc);
                    _processTimeHistogram.Record((int)metadata.ProcessingTime.TotalMilliseconds);
                    RecordSubHistogram(entry.Value, "processtime", (int)metadata.ProcessingTime.TotalMilliseconds);
                }
            }

            if (Completed != null) {
                var args = new CompletedEventArgs<T> { Queue = this, Entry = entry };
                await Completed.InvokeAsync(this, args).AnyContext();
            }
        }

        public AsyncEvent<AbandonedEventArgs<T>> Abandoned { get; } = new AsyncEvent<AbandonedEventArgs<T>>(true);

        protected virtual async Task OnAbandonedAsync(IQueueEntry<T> entry) {
            LastDequeueActivity = SystemClock.UtcNow;

            _abandonedCounter.Add(1);
            IncrementSubCounter(entry.Value, "abandoned");

            if (entry is QueueEntry<T> metadata && metadata.DequeuedTimeUtc > DateTime.MinValue) {
                metadata.ProcessingTime = SystemClock.UtcNow.Subtract(metadata.DequeuedTimeUtc);
                _processTimeHistogram.Record((int)metadata.ProcessingTime.TotalMilliseconds);
                RecordSubHistogram(entry.Value, "processtime", (int)metadata.ProcessingTime.TotalMilliseconds);
            }

            if (Abandoned != null) {
                var args = new AbandonedEventArgs<T> { Queue = this, Entry = entry };
                await Abandoned.InvokeAsync(this, args).AnyContext();
            }
        }

        private string GetMetricsPrefix() {
            var metricsPrefix = _options.MetricsPrefix;
            if (!String.IsNullOrEmpty(metricsPrefix) && !metricsPrefix.EndsWith("."))
                metricsPrefix += ".";

            metricsPrefix += typeof(T).Name.ToLowerInvariant();

            return metricsPrefix;
        }

        protected string GetSubMetricName(T data) {
            var haveStatName = data as IHaveSubMetricName;
            return haveStatName?.SubMetricName;
        }

        protected readonly ConcurrentDictionary<string, Counter<int>> _counters = new();
        private void IncrementSubCounter(T data, string name) {
            if (data is not IHaveSubMetricName)
                return;

            string subMetricName = GetSubMetricName(data);
            if (String.IsNullOrEmpty(subMetricName))
                return;

            var fullName = GetFullMetricName(subMetricName, name);
            _counters.GetOrAdd(fullName, FoundatioDiagnostics.Meter.CreateCounter<int>(fullName)).Add(1);
        }

        protected readonly ConcurrentDictionary<string, Histogram<int>> _histograms = new();
        private void RecordSubHistogram(T data, string name, int value) {
            if (data is not IHaveSubMetricName)
                return;

            string subMetricName = GetSubMetricName(data);
            if (String.IsNullOrEmpty(subMetricName))
                return;

            var fullName = GetFullMetricName(subMetricName, name);
            _histograms.GetOrAdd(fullName, FoundatioDiagnostics.Meter.CreateHistogram<int>(fullName)).Record(value);
        }

        protected string GetFullMetricName(string name) {
            return String.Concat(_metricsPrefix, ".", name);
        }

        protected string GetFullMetricName(string customMetricName, string name) {
            return String.IsNullOrEmpty(customMetricName) ? GetFullMetricName(name) : String.Concat(_metricsPrefix, ".", customMetricName.ToLower(), ".", name);
        }

        private void EnsureQueueStatsTimer() {
            if (_timer == null)
                _timer = new ScheduledTimer(GetQueueStats, minimumIntervalTime: TimeSpan.FromMilliseconds(250), loggerFactory: _options?.LoggerFactory ?? NullLoggerFactory.Instance);
        }

        protected virtual long GetQueueCount() {
            EnsureQueueStatsTimer();
            return _count;
        }

        protected virtual long GetWorkingCount() {
            EnsureQueueStatsTimer();
            return _working;
        }

        protected virtual long GetDeadletterCount() {
            EnsureQueueStatsTimer();
            return _deadletter;
        }

        private long _count = 0;
        private long _working = 0;
        private long _deadletter = 0;

        private async Task<DateTime?> GetQueueStats() {
            try {
                var stats = await GetQueueStatsAsync().AnyContext();
                _logger.LogTrace("Getting queue stats");

                _count = stats.Queued;
                _working = stats.Working;
                _deadletter = stats.Deadletter;
            } catch (Exception ex) {
                _logger.LogError(ex, "Error getting queue stats");
            }

            return null;
        }

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
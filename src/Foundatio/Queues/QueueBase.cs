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

namespace Foundatio.Queues;

public abstract class QueueBase<T, TOptions> : MaintenanceBase, IQueue<T>, IHaveTimeProvider, IQueueActivity where T : class where TOptions : SharedQueueOptions<T>
{
    protected readonly TOptions _options;
    private readonly string _metricsPrefix;
    protected readonly ISerializer _serializer;

    private readonly Counter<long> _enqueuedCounter;
    private readonly Counter<long> _dequeuedCounter;
    private readonly Histogram<double> _queueTimeHistogram;
    private readonly Counter<long> _completedCounter;
    private readonly Histogram<double> _processTimeHistogram;
    private readonly Histogram<double> _totalTimeHistogram;
    private readonly Counter<long> _abandonedCounter;
#pragma warning disable IDE0052 // Remove unread private members
    private readonly ObservableGauge<long> _countGauge;
    private readonly ObservableGauge<long> _workingGauge;
    private readonly ObservableGauge<long> _deadletterGauge;
#pragma warning restore IDE0052 // Remove unread private members
    private readonly TagList _emptyTags = default;

    private readonly List<IQueueBehavior<T>> _behaviors = new();
    protected readonly CancellationTokenSource _queueDisposedCancellationTokenSource;
    private bool _isDisposed;

    protected QueueBase(TOptions options) : base(options?.TimeProvider, options?.LoggerFactory)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _metricsPrefix = $"foundatio.{typeof(T).Name.ToLowerInvariant()}";

        QueueId = options.Name + Guid.NewGuid().ToString("N").Substring(10);

        _serializer = options.Serializer ?? DefaultSerializer.Instance;
        options.Behaviors.ForEach(AttachBehavior);

        _queueDisposedCancellationTokenSource = new CancellationTokenSource();

        // setup meters
        _enqueuedCounter = FoundatioDiagnostics.Meter.CreateCounter<long>(GetFullMetricName("enqueued"), description: "Number of enqueued items");
        _dequeuedCounter = FoundatioDiagnostics.Meter.CreateCounter<long>(GetFullMetricName("dequeued"), description: "Number of dequeued items");
        _queueTimeHistogram = FoundatioDiagnostics.Meter.CreateHistogram<double>(GetFullMetricName("queuetime"), description: "Time in queue", unit: "ms");
        _completedCounter = FoundatioDiagnostics.Meter.CreateCounter<long>(GetFullMetricName("completed"), description: "Number of completed items");
        _processTimeHistogram = FoundatioDiagnostics.Meter.CreateHistogram<double>(GetFullMetricName("processtime"), description: "Time to process items", unit: "ms");
        _totalTimeHistogram = FoundatioDiagnostics.Meter.CreateHistogram<double>(GetFullMetricName("totaltime"), description: "Total time in queue", unit: "ms");
        _abandonedCounter = FoundatioDiagnostics.Meter.CreateCounter<long>(GetFullMetricName("abandoned"), description: "Number of abandoned items");

        var queueMetricValues = new InstrumentsValues<long, long, long>(() =>
        {
            try
            {
                var stats = GetMetricsQueueStats();
                return (stats.Queued, stats.Working, stats.Deadletter);
            }
            catch
            {
                return (0, 0, 0);
            }
        });

        _countGauge = FoundatioDiagnostics.Meter.CreateObservableGauge(GetFullMetricName("count"), () => new Measurement<long>(queueMetricValues.GetValue1()), description: "Number of items in the queue");
        _workingGauge = FoundatioDiagnostics.Meter.CreateObservableGauge(GetFullMetricName("working"), () => new Measurement<long>(queueMetricValues.GetValue2()), description: "Number of items currently being processed");
        _deadletterGauge = FoundatioDiagnostics.Meter.CreateObservableGauge(GetFullMetricName("deadletter"), () => new Measurement<long>(queueMetricValues.GetValue3()), description: "Number of items in the deadletter queue");
    }

    public string QueueId { get; protected set; }
    public DateTimeOffset? LastEnqueueActivity { get; protected set; }
    public DateTimeOffset? LastDequeueActivity { get; protected set; }
    ISerializer IHaveSerializer.Serializer => _serializer;
    TimeProvider IHaveTimeProvider.TimeProvider => _timeProvider;

    public void AttachBehavior(IQueueBehavior<T> behavior)
    {
        if (behavior == null)
            return;

        _behaviors.Add(behavior);
        behavior.Attach(this);
    }

    protected abstract Task EnsureQueueCreatedAsync(CancellationToken cancellationToken = default);

    protected abstract Task<string> EnqueueImplAsync(T data, QueueEntryOptions options);
    public async Task<string> EnqueueAsync(T data, QueueEntryOptions options = null)
    {
        await EnsureQueueCreatedAsync(_queueDisposedCancellationTokenSource.Token).AnyContext();

        LastEnqueueActivity = _timeProvider.GetUtcNow();
        options ??= new QueueEntryOptions();

        return await EnqueueImplAsync(data, options).AnyContext();
    }

    protected abstract Task<IQueueEntry<T>> DequeueImplAsync(CancellationToken linkedCancellationToken);
    public async Task<IQueueEntry<T>> DequeueAsync(CancellationToken cancellationToken)
    {
        await EnsureQueueCreatedAsync(_queueDisposedCancellationTokenSource.Token).AnyContext();

        LastDequeueActivity = _timeProvider.GetUtcNow();
        using var linkedCancellationTokenSource = GetLinkedDisposableCancellationTokenSource(cancellationToken);
        return await DequeueImplAsync(linkedCancellationTokenSource.Token).AnyContext();
    }

    public virtual async Task<IQueueEntry<T>> DequeueAsync(TimeSpan? timeout = null)
    {
        using var timeoutCancellationTokenSource = timeout.ToCancellationTokenSource(TimeSpan.FromSeconds(30));
        return await DequeueAsync(timeoutCancellationTokenSource.Token).AnyContext();
    }

    public abstract Task RenewLockAsync(IQueueEntry<T> queueEntry);

    public abstract Task CompleteAsync(IQueueEntry<T> queueEntry);

    public abstract Task AbandonAsync(IQueueEntry<T> queueEntry);

    protected abstract Task<IEnumerable<T>> GetDeadletterItemsImplAsync(CancellationToken cancellationToken);
    public async Task<IEnumerable<T>> GetDeadletterItemsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureQueueCreatedAsync(_queueDisposedCancellationTokenSource.Token).AnyContext();
        return await GetDeadletterItemsImplAsync(cancellationToken).AnyContext();
    }

    protected abstract Task<QueueStats> GetQueueStatsImplAsync();

    public Task<QueueStats> GetQueueStatsAsync()
    {
        return GetQueueStatsImplAsync();
    }

    protected virtual QueueStats GetMetricsQueueStats()
    {
        return GetQueueStatsAsync().GetAwaiter().GetResult();
    }

    public abstract Task DeleteQueueAsync();

    protected abstract void StartWorkingImpl(Func<IQueueEntry<T>, CancellationToken, Task> handler, bool autoComplete, CancellationToken cancellationToken);
    public async Task StartWorkingAsync(Func<IQueueEntry<T>, CancellationToken, Task> handler, bool autoComplete = false, CancellationToken cancellationToken = default)
    {
        await EnsureQueueCreatedAsync(_queueDisposedCancellationTokenSource.Token).AnyContext();
        StartWorkingImpl(handler, autoComplete, cancellationToken);
    }

    public IReadOnlyCollection<IQueueBehavior<T>> Behaviors => _behaviors;

    public AsyncEvent<EnqueuingEventArgs<T>> Enqueuing { get; } = new AsyncEvent<EnqueuingEventArgs<T>>();

    protected virtual async Task<bool> OnEnqueuingAsync(T data, QueueEntryOptions options)
    {
        if (String.IsNullOrEmpty(options.CorrelationId))
        {
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

    protected virtual Task OnEnqueuedAsync(IQueueEntry<T> entry)
    {
        LastEnqueueActivity = _timeProvider.GetUtcNow();

        var tags = GetQueueEntryTags(entry);
        _enqueuedCounter.Add(1, tags);
        IncrementSubCounter(entry.Value, "enqueued", tags);

        var enqueued = Enqueued;
        if (enqueued == null)
            return Task.CompletedTask;

        var args = new EnqueuedEventArgs<T> { Queue = this, Entry = entry };
        return enqueued.InvokeAsync(this, args);
    }

    public AsyncEvent<DequeuedEventArgs<T>> Dequeued { get; } = new AsyncEvent<DequeuedEventArgs<T>>(true);

    protected virtual Task OnDequeuedAsync(IQueueEntry<T> entry)
    {
        LastDequeueActivity = _timeProvider.GetUtcNow();

        var tags = GetQueueEntryTags(entry);
        _dequeuedCounter.Add(1, tags);
        IncrementSubCounter(entry.Value, "dequeued", tags);

        var metadata = entry as IQueueEntryMetadata;
        if (metadata != null && (metadata.EnqueuedTimeUtc != DateTime.MinValue || metadata.DequeuedTimeUtc != DateTime.MinValue))
        {
            var start = metadata.EnqueuedTimeUtc;
            var end = metadata.DequeuedTimeUtc;
            double time = (end - start).TotalMilliseconds;

            _queueTimeHistogram.Record(time, tags);
            RecordSubHistogram(entry.Value, "queuetime", time, tags);
        }

        var dequeued = Dequeued;
        if (dequeued == null)
            return Task.CompletedTask;

        var args = new DequeuedEventArgs<T> { Queue = this, Entry = entry };
        return dequeued.InvokeAsync(this, args);
    }

    protected virtual TagList GetQueueEntryTags(IQueueEntry<T> entry)
    {
        return _emptyTags;
    }

    public AsyncEvent<LockRenewedEventArgs<T>> LockRenewed { get; } = new AsyncEvent<LockRenewedEventArgs<T>>(true);

    protected virtual Task OnLockRenewedAsync(IQueueEntry<T> entry)
    {
        LastDequeueActivity = _timeProvider.GetUtcNow();

        var lockRenewed = LockRenewed;
        if (lockRenewed == null)
            return Task.CompletedTask;

        var args = new LockRenewedEventArgs<T> { Queue = this, Entry = entry };
        return lockRenewed.InvokeAsync(this, args);
    }

    public AsyncEvent<CompletedEventArgs<T>> Completed { get; } = new AsyncEvent<CompletedEventArgs<T>>(true);

    protected virtual async Task OnCompletedAsync(IQueueEntry<T> entry)
    {
        var now = _timeProvider.GetUtcNow();
        LastDequeueActivity = now;

        var tags = GetQueueEntryTags(entry);
        _completedCounter.Add(1, tags);
        IncrementSubCounter(entry.Value, "completed", tags);

        if (entry is QueueEntry<T> metadata)
        {
            if (metadata.EnqueuedTimeUtc > DateTime.MinValue)
            {
                metadata.TotalTime = now.Subtract(metadata.EnqueuedTimeUtc);
                _totalTimeHistogram.Record((int)metadata.TotalTime.TotalMilliseconds, tags);
                RecordSubHistogram(entry.Value, "totaltime", (int)metadata.TotalTime.TotalMilliseconds, tags);
            }

            if (metadata.DequeuedTimeUtc > DateTime.MinValue)
            {
                metadata.ProcessingTime = now.Subtract(metadata.DequeuedTimeUtc);
                _processTimeHistogram.Record((int)metadata.ProcessingTime.TotalMilliseconds, tags);
                RecordSubHistogram(entry.Value, "processtime", (int)metadata.ProcessingTime.TotalMilliseconds, tags);
            }
        }

        if (Completed != null)
        {
            var args = new CompletedEventArgs<T> { Queue = this, Entry = entry };
            await Completed.InvokeAsync(this, args).AnyContext();
        }
    }

    public AsyncEvent<AbandonedEventArgs<T>> Abandoned { get; } = new AsyncEvent<AbandonedEventArgs<T>>(true);

    protected virtual async Task OnAbandonedAsync(IQueueEntry<T> entry)
    {
        LastDequeueActivity = _timeProvider.GetUtcNow();

        var tags = GetQueueEntryTags(entry);
        _abandonedCounter.Add(1, tags);
        IncrementSubCounter(entry.Value, "abandoned", tags);

        if (entry is QueueEntry<T> metadata && metadata.DequeuedTimeUtc > DateTime.MinValue)
        {
            metadata.ProcessingTime = _timeProvider.GetUtcNow().Subtract(metadata.DequeuedTimeUtc);
            _processTimeHistogram.Record((int)metadata.ProcessingTime.TotalMilliseconds, tags);
            RecordSubHistogram(entry.Value, "processtime", (int)metadata.ProcessingTime.TotalMilliseconds, tags);
        }

        if (Abandoned != null)
        {
            var args = new AbandonedEventArgs<T> { Queue = this, Entry = entry };
            await Abandoned.InvokeAsync(this, args).AnyContext();
        }
    }

    protected string GetSubMetricName(T data)
    {
        var haveStatName = data as IHaveSubMetricName;
        return haveStatName?.SubMetricName;
    }

    protected readonly ConcurrentDictionary<string, Counter<long>> _counters = new();
    private void IncrementSubCounter(T data, string name, in TagList tags)
    {
        if (data is not IHaveSubMetricName)
            return;

        string subMetricName = GetSubMetricName(data);
        if (String.IsNullOrEmpty(subMetricName))
            return;

        var fullName = GetFullMetricName(subMetricName, name);
        _counters.GetOrAdd(fullName, FoundatioDiagnostics.Meter.CreateCounter<long>(fullName)).Add(1, tags);
    }

    protected readonly ConcurrentDictionary<string, Histogram<double>> _histograms = new();
    private void RecordSubHistogram(T data, string name, double value, in TagList tags)
    {
        if (data is not IHaveSubMetricName)
            return;

        string subMetricName = GetSubMetricName(data);
        if (String.IsNullOrEmpty(subMetricName))
            return;

        var fullName = GetFullMetricName(subMetricName, name);
        _histograms.GetOrAdd(fullName, FoundatioDiagnostics.Meter.CreateHistogram<double>(fullName)).Record(value, tags);
    }

    protected string GetFullMetricName(string name)
    {
        return String.Concat(_metricsPrefix, ".", name);
    }

    protected string GetFullMetricName(string customMetricName, string name)
    {
        return String.IsNullOrEmpty(customMetricName) ? GetFullMetricName(name) : String.Concat(_metricsPrefix, ".", customMetricName.ToLower(), ".", name);
    }

    protected CancellationTokenSource GetLinkedDisposableCancellationTokenSource(CancellationToken cancellationToken)
    {
        return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _queueDisposedCancellationTokenSource.Token);
    }

    public override void Dispose()
    {
        if (_isDisposed)
        {
            _logger.LogTrace("Queue {Name} ({Id})  dispose was already called", _options.Name, QueueId);
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

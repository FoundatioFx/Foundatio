using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Metrics;
using Foundatio.Resilience;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Queues;

public abstract class QueueBase<T, TOptions> : MaintenanceBase, IQueue<T>, IHaveTimeProvider, IQueueActivity where T : class where TOptions : SharedQueueOptions<T>
{
    protected readonly TOptions _options;
    private readonly string _metricsPrefix;
    protected readonly ISerializer _serializer;
    protected readonly IResiliencePolicy _resiliencePolicy;

    private readonly Counter<long> _enqueuedCounter;
    private readonly Counter<long> _dequeuedCounter;
    private readonly Histogram<double> _queueTimeHistogram;
    private readonly Counter<long> _completedCounter;
    private readonly Histogram<double> _processTimeHistogram;
    private readonly Histogram<double> _totalTimeHistogram;
    private readonly Counter<long> _abandonedCounter;
#pragma warning disable IDE0052 // Remove unread private members
    private readonly ObservableGauge<long>? _countGauge;
    private readonly ObservableGauge<long>? _workingGauge;
    private readonly ObservableGauge<long>? _deadletterGauge;
#pragma warning restore IDE0052 // Remove unread private members
    private readonly TagList _emptyTags = default;

    private readonly List<IQueueBehavior<T>> _behaviors = new();
    private QueueStats? _queueStats;
    private DateTimeOffset _nextQueueStatsUpdate = DateTimeOffset.MinValue;
    private int _groupIdUnsupportedLogged;
    private long _workerErrorCount;

    private static readonly Func<int, TimeSpan> _workerErrorBackoff = ResiliencePolicy.ExponentialDelay(TimeSpan.FromSeconds(1));
    private static readonly TimeSpan _maxWorkerErrorDelay = TimeSpan.FromSeconds(30);

    protected QueueBase(TOptions options) : base(options?.TimeProvider, options?.LoggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _metricsPrefix = $"foundatio.{typeof(T).Name.ToLowerInvariant()}";
        if (!String.IsNullOrWhiteSpace(options.MetricsPrefix))
            _metricsPrefix = $"{_metricsPrefix}.{options.MetricsPrefix.Trim()}";

        QueueId = $"{options.Name.Trim()}{Guid.NewGuid().ToString("N").Substring(10)}";

        _serializer = options.Serializer;
        options.Behaviors.ForEach(AttachBehavior);

        var resiliencePolicyProvider = _options.GetResiliencePolicyProvider() ?? DefaultResiliencePolicyProvider.Instance;
        _resiliencePolicy = resiliencePolicyProvider.GetPolicy<QueueBase<T, TOptions>, IQueue<T>, IQueue>(_logger, _timeProvider);

        // setup meters
        _enqueuedCounter = FoundatioDiagnostics.Meter.CreateCounter<long>(GetFullMetricName("enqueued"), description: "Number of enqueued items");
        _dequeuedCounter = FoundatioDiagnostics.Meter.CreateCounter<long>(GetFullMetricName("dequeued"), description: "Number of dequeued items");
        _queueTimeHistogram = FoundatioDiagnostics.Meter.CreateHistogram<double>(GetFullMetricName("queuetime"), description: "Time in queue", unit: "ms");
        _completedCounter = FoundatioDiagnostics.Meter.CreateCounter<long>(GetFullMetricName("completed"), description: "Number of completed items");
        _processTimeHistogram = FoundatioDiagnostics.Meter.CreateHistogram<double>(GetFullMetricName("processtime"), description: "Time to process items", unit: "ms");
        _totalTimeHistogram = FoundatioDiagnostics.Meter.CreateHistogram<double>(GetFullMetricName("totaltime"), description: "Total time in queue", unit: "ms");
        _abandonedCounter = FoundatioDiagnostics.Meter.CreateCounter<long>(GetFullMetricName("abandoned"), description: "Number of abandoned items");

        if (!options.MetricsPollingEnabled)
            return;

        var queueMetricValues = new InstrumentsValues<long, long, long>(() =>
        {
            if (IsDisposed || (options.MetricsPollingInterval > TimeSpan.Zero && _nextQueueStatsUpdate >= _timeProvider.GetUtcNow()))
            {
                if (_queueStats is not null)
                {
                    _logger.LogTrace("Using cached queue stats for {QueueName} ({QueueId})", _options.Name, QueueId);
                    return (_queueStats.Queued, _queueStats.Working, _queueStats.Deadletter);
                }

                _logger.LogTrace("Returning default queue stats for {QueueName} ({QueueId})", _options.Name, QueueId);
                return (0, 0, 0);
            }

            _nextQueueStatsUpdate = _timeProvider.GetUtcNow().Add(_options.MetricsPollingInterval);
            _logger.LogTrace("Getting metrics queue stats for {QueueName} ({QueueId}): Next update scheduled for {NextQueueStatsUpdate:O}", _options.Name, QueueId, _nextQueueStatsUpdate);
            using var activity = FoundatioDiagnostics.ActivitySource.StartActivity("Queue Stats: " + _options.Name);
            try
            {
                _queueStats = GetMetricsQueueStats();
                return (_queueStats.Queued, _queueStats.Working, _queueStats.Deadletter);
            }
            catch (Exception ex)
            {
                activity?.SetErrorStatus(ex);
                _logger.LogError(ex, "Error getting queue metrics for {QueueName} ({QueueId}): {Message}", _options.Name, QueueId, ex.Message);
                return (0, 0, 0);
            }
        }, _logger);

        _countGauge = FoundatioDiagnostics.Meter.CreateObservableGauge(GetFullMetricName("count"),
            () => IsDisposed ? Array.Empty<Measurement<long>>() : [new Measurement<long>(queueMetricValues.GetValue1())],
            description: "Number of items in the queue");
        _workingGauge = FoundatioDiagnostics.Meter.CreateObservableGauge(GetFullMetricName("working"),
            () => IsDisposed ? Array.Empty<Measurement<long>>() : [new Measurement<long>(queueMetricValues.GetValue2())],
            description: "Number of items currently being processed");
        _deadletterGauge = FoundatioDiagnostics.Meter.CreateObservableGauge(GetFullMetricName("deadletter"),
            () => IsDisposed ? Array.Empty<Measurement<long>>() : [new Measurement<long>(queueMetricValues.GetValue3())],
            description: "Number of items in the deadletter queue");
    }

    public string QueueId { get; init; }
    public DateTimeOffset? LastEnqueueActivity { get; protected set; }
    public DateTimeOffset? LastDequeueActivity { get; protected set; }
    ISerializer IHaveSerializer.Serializer => _serializer;
    TimeProvider IHaveTimeProvider.TimeProvider => _timeProvider;

    public void AttachBehavior(IQueueBehavior<T> behavior)
    {
        ArgumentNullException.ThrowIfNull(behavior);

        _behaviors.Add(behavior);
        behavior.Attach(this);
    }

    /// <summary>
    /// Called before queue operations to ensure the queue exists. The <paramref name="cancellationToken"/>
    /// is always <see cref="MaintenanceBase.DisposedCancellationToken"/>; queue creation should only
    /// abort when the queue is being disposed, never due to an individual caller's cancellation.
    /// </summary>
    protected abstract Task EnsureQueueCreatedAsync(CancellationToken cancellationToken = default);

    protected abstract Task<string?> EnqueueImplAsync(T data, QueueEntryOptions options);
    public async Task<string?> EnqueueAsync(T data, QueueEntryOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(data);

        await EnsureQueueCreatedAsync(DisposedCancellationToken).AnyContext();

        LastEnqueueActivity = _timeProvider.GetUtcNow();
        options = CreateEnqueueOptions(data, options);

        if (options.GroupId is not null && !SupportsGroupId && Interlocked.Exchange(ref _groupIdUnsupportedLogged, 1) == 0)
            _logger.LogDebug("Queue {QueueName} ({QueueType}) does not use GroupId for delivery order or fairness", _options.Name, GetType().Name);

        return await EnqueueImplAsync(data, options).AnyContext();
    }

    /// <summary>
    /// Copies the caller's options so enqueue never mutates them: correlation and trace metadata and
    /// <see cref="Enqueuing"/> handler changes apply to this message only, and a reused options instance stays reusable.
    /// The group id resolver and <see cref="Activity"/> defaults are applied here, before <see cref="EnqueueImplAsync"/>,
    /// so providers can validate the final values before <see cref="Enqueuing"/> handlers run.
    /// </summary>
    private QueueEntryOptions CreateEnqueueOptions(T data, QueueEntryOptions? options)
    {
        options = options is null
            ? new QueueEntryOptions()
            : options with { Properties = CopyProperties(options.Properties) };

        options.GroupId ??= _options.GroupIdResolver?.Invoke(data);

        if (String.IsNullOrEmpty(options.CorrelationId))
        {
            options.CorrelationId = Activity.Current?.Id;
            if (!String.IsNullOrEmpty(Activity.Current?.TraceStateString))
                options.Properties.TryAdd("TraceState", Activity.Current.TraceStateString);
        }

        return options;
    }

    private static IDictionary<string, string> CopyProperties(IDictionary<string, string> properties)
    {
        return properties switch
        {
            Dictionary<string, string> dictionary => new Dictionary<string, string>(dictionary, dictionary.Comparer),
            SortedDictionary<string, string> dictionary => new SortedDictionary<string, string>(dictionary, dictionary.Comparer),
            SortedList<string, string> dictionary => new SortedList<string, string>(dictionary, dictionary.Comparer),
            ConcurrentDictionary<string, string> dictionary => new ConcurrentDictionary<string, string>(dictionary, dictionary.Comparer),
            _ => new Dictionary<string, string>(properties)
        };
    }

    /// <summary>
    /// Whether the provider uses <see cref="QueueEntryOptions.GroupId"/> to affect delivery (for example fairness or ordering).
    /// When <c>false</c> (the default), a single debug message is logged the first time a group id is enqueued.
    /// Providers that return <c>false</c> may still store the value and return it on <see cref="IQueueEntry.GroupId"/>.
    /// </summary>
    protected virtual bool SupportsGroupId => false;

    protected abstract Task<IQueueEntry<T>?> DequeueImplAsync(CancellationToken linkedCancellationToken);
    public async Task<IQueueEntry<T>?> DequeueAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        // Use DisposedCancellationToken for setup: callers may pass an already-cancelled token
        // (e.g. TimeSpan.Zero timeout) which should skip waiting, not prevent queue creation.
        await EnsureQueueCreatedAsync(DisposedCancellationToken).AnyContext();

        using var linkedCancellationTokenSource = GetLinkedDisposableCancellationTokenSource(cancellationToken);
        LastDequeueActivity = _timeProvider.GetUtcNow();
        return await DequeueImplAsync(linkedCancellationTokenSource.Token).AnyContext();
    }

    public virtual async Task<IQueueEntry<T>?> DequeueAsync(TimeSpan? timeout = null)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        using var timeoutCancellationTokenSource = timeout.ToCancellationTokenSource(TimeSpan.FromSeconds(30));
        return await DequeueAsync(timeoutCancellationTokenSource.Token).AnyContext();
    }

    public abstract Task RenewLockAsync(IQueueEntry<T> queueEntry);

    public abstract Task CompleteAsync(IQueueEntry<T> queueEntry);

    public abstract Task AbandonAsync(IQueueEntry<T> queueEntry);

    protected abstract Task<IEnumerable<T>> GetDeadletterItemsImplAsync(CancellationToken cancellationToken);
    public async Task<IEnumerable<T>> GetDeadletterItemsAsync(CancellationToken cancellationToken = default)
    {
        // Use DisposedCancellationToken for setup: queue creation should only abort on disposal,
        // not due to the caller's cancellation token.
        await EnsureQueueCreatedAsync(DisposedCancellationToken).AnyContext();

        using var linkedCancellationTokenSource = GetLinkedDisposableCancellationTokenSource(cancellationToken);
        return await GetDeadletterItemsImplAsync(linkedCancellationTokenSource.Token).AnyContext();
    }

    protected abstract Task<QueueStats> GetQueueStatsImplAsync();

    public async Task<QueueStats> GetQueueStatsAsync()
    {
        _logger.LogTrace("Getting queue stats for {QueueName} ({QueueId})", _options.Name, QueueId);
        _queueStats = await GetQueueStatsImplAsync().AnyContext();
        return _queueStats;
    }

    // TODO: sync-over-async — called from ObservableGauge callbacks (which must be synchronous),
    // so this blocks a thread-pool thread on async I/O for external providers (Redis, Azure, etc.).
    // The MetricsPollingInterval cache above mitigates frequency but doesn't eliminate the risk of
    // thread-pool starvation under load. Blocked on async gauge callback support in .NET:
    // https://github.com/dotnet/runtime/issues/96850
    protected virtual QueueStats GetMetricsQueueStats()
    {
        return GetQueueStatsAsync().AnyContext().GetAwaiter().GetResult();
    }

    protected abstract Task DeleteQueueImplAsync();

    public async Task DeleteQueueAsync()
    {
        _logger.LogTrace("Deleting queue: {QueueName} ({QueueId})", _options.Name, QueueId);
        await DeleteQueueImplAsync().AnyContext();
        await OnQueueDeletedAsync().AnyContext();
    }

    protected abstract void StartWorkingImpl(Func<IQueueEntry<T>, CancellationToken, Task> handler, bool autoComplete, CancellationToken cancellationToken);
    public async Task StartWorkingAsync(Func<IQueueEntry<T>, CancellationToken, Task> handler, bool autoComplete = false, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        // Use DisposedCancellationToken for setup: queue creation should only abort on disposal.
        // StartWorkingImpl creates its own linked token for the long-running worker loop.
        await EnsureQueueCreatedAsync(DisposedCancellationToken).AnyContext();
        StartWorkingImpl(handler, autoComplete, cancellationToken);
    }

    /// <summary>
    /// The number of worker errors since the queue stats were last reset. Providers report this as <see cref="QueueStats.Errors"/>.
    /// </summary>
    protected long WorkerErrorCount => Interlocked.Read(ref _workerErrorCount);

    /// <summary>
    /// Resets <see cref="WorkerErrorCount"/> to zero. Call this when resetting queue stats.
    /// </summary>
    protected void ResetWorkerErrorCount() => Interlocked.Exchange(ref _workerErrorCount, 0);

    /// <summary>
    /// Starts a background worker that dequeues entries with <see cref="DequeueImplAsync"/> and runs <paramref name="handler"/>
    /// for each one until <paramref name="cancellationToken"/> is cancelled or the queue is disposed.
    /// Providers call this from <see cref="StartWorkingImpl"/> so every implementation handles worker errors the same way.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Cancellation stops the worker and is not counted as an error.</item>
    /// <item>A dequeue failure is logged, counted in <see cref="WorkerErrorCount"/> and followed by an exponential backoff with jitter
    /// (about 1 second, doubling up to 30 seconds). The backoff resets after the next dequeue that doesn't throw.</item>
    /// <item>A handler failure is logged and counted, and the entry is abandoned unless the handler already completed or abandoned it.
    /// A handler that throws <see cref="OperationCanceledException"/> because the worker was cancelled isn't counted.</item>
    /// <item>Abandon and auto-complete run through the queue's resilience policy. If they still fail, the error is logged
    /// (auto-complete failures are also counted) and the worker keeps running; the entry becomes available again when its lock expires.</item>
    /// <item>An entry dequeued after cancellation was requested isn't processed; it becomes available again when its lock expires.</item>
    /// </list>
    /// </remarks>
    /// <returns>A task that completes when the worker stops.</returns>
    protected Task StartWorker(Func<IQueueEntry<T>, CancellationToken, Task> handler, bool autoComplete, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);

        _logger.LogTrace("Queue {QueueName} start working", _options.Name);

        var linkedCancellationTokenSource = GetLinkedDisposableCancellationTokenSource(cancellationToken);
        return Task.Run(() => RunWorkerAsync(handler, autoComplete, linkedCancellationTokenSource.Token), linkedCancellationTokenSource.Token)
            .ContinueWith(_ => linkedCancellationTokenSource.Dispose(), TaskScheduler.Default);
    }

    private async Task RunWorkerAsync(Func<IQueueEntry<T>, CancellationToken, Task> handler, bool autoComplete, CancellationToken cancellationToken)
    {
        _logger.LogTrace("WorkerLoop Start {QueueName}", _options.Name);

        int consecutiveDequeueErrors = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            IQueueEntry<T>? entry;
            try
            {
                entry = await DequeueImplAsync(cancellationToken).AnyContext();
                consecutiveDequeueErrors = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveDequeueErrors++;
                Interlocked.Increment(ref _workerErrorCount);

                var delay = GetWorkerErrorDelay(consecutiveDequeueErrors);
                _logger.LogError(ex, "Error dequeuing from {QueueName}, retrying in {Delay:g}: {Message}", _options.Name, delay, ex.Message);
                await _timeProvider.SafeDelay(delay, cancellationToken).AnyContext();
                continue;
            }

            if (entry is null || cancellationToken.IsCancellationRequested)
                continue;

            await ProcessWorkerEntryAsync(entry, handler, autoComplete, cancellationToken).AnyContext();
        }

        _logger.LogTrace("Worker exiting: {QueueName} Cancel Requested: {IsCancellationRequested}", _options.Name, cancellationToken.IsCancellationRequested);
    }

    private async Task ProcessWorkerEntryAsync(IQueueEntry<T> entry, Func<IQueueEntry<T>, CancellationToken, Task> handler, bool autoComplete, CancellationToken cancellationToken)
    {
        try
        {
            await handler(entry, cancellationToken).AnyContext();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Worker cancelled while processing queue entry {QueueEntryId}", entry.Id);
            await AbandonWorkerEntryAsync(entry).AnyContext();
            return;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _workerErrorCount);
            _logger.LogError(ex, "Worker error: {Message}", ex.Message);
            await AbandonWorkerEntryAsync(entry).AnyContext();
            return;
        }

        if (!autoComplete)
            return;

        try
        {
            await _resiliencePolicy.ExecuteAsync(async _ =>
            {
                if (!entry.IsAbandoned && !entry.IsCompleted)
                    await entry.CompleteAsync().AnyContext();
            }, DisposedCancellationToken).AnyContext();
        }
        catch (OperationCanceledException) when (DisposedCancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Queue disposed before entry {QueueEntryId} could be auto completed", entry.Id);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _workerErrorCount);
            _logger.LogError(ex, "Worker error attempting to auto complete entry {QueueEntryId}: {Message}", entry.Id, ex.Message);
        }
    }

    private async Task AbandonWorkerEntryAsync(IQueueEntry<T> entry)
    {
        try
        {
            await _resiliencePolicy.ExecuteAsync(async _ =>
            {
                if (!entry.IsAbandoned && !entry.IsCompleted)
                    await entry.AbandonAsync().AnyContext();
            }, DisposedCancellationToken).AnyContext();
        }
        catch (OperationCanceledException) when (DisposedCancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Queue disposed before entry {QueueEntryId} could be abandoned", entry.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Worker error abandoning queue entry {QueueEntryId}: {Message}", entry.Id, ex.Message);
        }
    }

    private static TimeSpan GetWorkerErrorDelay(int consecutiveErrors)
    {
        var delay = _workerErrorBackoff(Math.Min(consecutiveErrors, 16));
        double jitter = delay.TotalMilliseconds * 0.5 * (Random.Shared.NextDouble() - 0.5);
        delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds + jitter);

        return delay > _maxWorkerErrorDelay ? _maxWorkerErrorDelay : delay;
    }

    public IReadOnlyCollection<IQueueBehavior<T>> Behaviors => _behaviors;

    public AsyncEvent<EnqueuingEventArgs<T>> Enqueuing { get; } = new AsyncEvent<EnqueuingEventArgs<T>>();

    protected virtual async Task<bool> OnEnqueuingAsync(T data, QueueEntryOptions options)
    {
        var enqueueing = Enqueuing;
        if (enqueueing is null)
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
        if (enqueued is null)
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
        if (dequeued is null)
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
        if (lockRenewed is null)
            return Task.CompletedTask;

        var args = new LockRenewedEventArgs<T> { Queue = this, Entry = entry };
        return lockRenewed.InvokeAsync(this, args);
    }

    public AsyncEvent<CompletedEventArgs<T>> Completed { get; } = new AsyncEvent<CompletedEventArgs<T>>(true);

    protected virtual async Task OnCompletedAsync(IQueueEntry<T> entry)
    {
        var utcNow = _timeProvider.GetUtcNow();
        LastDequeueActivity = utcNow;

        var tags = GetQueueEntryTags(entry);
        _completedCounter.Add(1, tags);
        IncrementSubCounter(entry.Value, "completed", tags);

        if (entry is QueueEntry<T> metadata)
        {
            if (metadata.EnqueuedTimeUtc > DateTime.MinValue)
            {
                metadata.TotalTime = utcNow.Subtract(metadata.EnqueuedTimeUtc);
                _totalTimeHistogram.Record((int)metadata.TotalTime.TotalMilliseconds, tags);
                RecordSubHistogram(entry.Value, "totaltime", (int)metadata.TotalTime.TotalMilliseconds, tags);
            }

            if (metadata.DequeuedTimeUtc > DateTime.MinValue)
            {
                metadata.ProcessingTime = utcNow.Subtract(metadata.DequeuedTimeUtc);
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

    public AsyncEvent<QueueDeletedEventArgs<T>> QueueDeleted { get; } = new AsyncEvent<QueueDeletedEventArgs<T>>(true);

    protected virtual async Task OnQueueDeletedAsync()
    {
        if (QueueDeleted is not null)
        {
            var args = new QueueDeletedEventArgs<T> { Queue = this };
            await QueueDeleted.InvokeAsync(this, args).AnyContext();
        }
    }

    protected string? GetSubMetricName(T? data)
    {
        var haveStatName = data as IHaveSubMetricName;
        return haveStatName?.SubMetricName;
    }

    protected readonly ConcurrentDictionary<string, Counter<long>> _counters = new();
    private void IncrementSubCounter(T? data, string name, in TagList tags)
    {
        if (data is not IHaveSubMetricName)
            return;

        string? subMetricName = GetSubMetricName(data);
        if (String.IsNullOrEmpty(subMetricName))
            return;

        var fullName = GetFullMetricName(subMetricName, name);
        _counters.GetOrAdd(fullName, FoundatioDiagnostics.Meter.CreateCounter<long>(fullName)).Add(1, tags);
    }

    protected readonly ConcurrentDictionary<string, Histogram<double>> _histograms = new();
    private void RecordSubHistogram(T? data, string name, double value, in TagList tags)
    {
        if (data is not IHaveSubMetricName)
            return;

        string? subMetricName = GetSubMetricName(data);
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
        return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, DisposedCancellationToken);
    }

    public override void Dispose()
    {
        _logger.LogTrace("Queue {QueueName} ({QueueId}) dispose", _options.Name, QueueId);
        SignalDispose();

        Abandoned?.Dispose();
        Completed?.Dispose();
        Dequeued?.Dispose();
        Enqueued?.Dispose();
        Enqueuing?.Dispose();
        LockRenewed?.Dispose();
        QueueDeleted?.Dispose();

        foreach (var behavior in _behaviors.OfType<IDisposable>())
            behavior.Dispose();

        _behaviors.Clear();
        base.Dispose();
    }
}


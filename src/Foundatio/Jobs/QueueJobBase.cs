using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Lock;
using Foundatio.Queues;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Jobs;

public abstract class QueueJobBase<T> : IQueueJob<T>, IHaveLogger, IHaveTimeProvider where T : class
{
    protected readonly ILogger _logger;
    protected readonly Lazy<IQueue<T>> _queue;
    protected readonly TimeProvider _timeProvider;
    protected readonly string _queueEntryName = typeof(T).Name;

    public QueueJobBase(Lazy<IQueue<T>> queue, TimeProvider timeProvider = null, ILoggerFactory loggerFactory = null)
    {
        _queue = queue;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = loggerFactory?.CreateLogger(GetType()) ?? NullLogger.Instance;
        AutoComplete = true;
    }

    public QueueJobBase(IQueue<T> queue, TimeProvider timeProvider = null, ILoggerFactory loggerFactory = null) : this(new Lazy<IQueue<T>>(() => queue), timeProvider, loggerFactory) { }

    protected bool AutoComplete { get; set; }
    public string JobId { get; } = Guid.NewGuid().ToString("N").Substring(0, 10);
    IQueue<T> IQueueJob<T>.Queue => _queue.Value;
    ILogger IHaveLogger.Logger => _logger;
    TimeProvider IHaveTimeProvider.TimeProvider => _timeProvider;

    public virtual async Task<JobResult> RunAsync(CancellationToken cancellationToken = default)
    {
        IQueueEntry<T> queueEntry;

        using var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            queueEntry = await _queue.Value.DequeueAsync(linkedCancellationTokenSource.Token).AnyContext();
        }
        catch (OperationCanceledException)
        {
            return JobResult.Cancelled;
        }
        catch (Exception ex)
        {
            return JobResult.FromException(ex, $"Error trying to dequeue message: {ex.Message}");
        }

        return await ProcessAsync(queueEntry, cancellationToken).AnyContext();
    }

    public async Task<JobResult> ProcessAsync(IQueueEntry<T> queueEntry, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested && queueEntry == null)
            return JobResult.Cancelled;

        if (queueEntry == null)
            return JobResult.CancelledWithMessage("No queue entry to process.");

        using var activity = StartProcessQueueEntryActivity(queueEntry);
        using var _ = _logger.BeginScope(s => s
            .Property("JobId", JobId)
            .Property("QueueEntryId", queueEntry.Id)
            .PropertyIf("CorrelationId", queueEntry.CorrelationId, !String.IsNullOrEmpty(queueEntry.CorrelationId))
            .Property("QueueEntryName", _queueEntryName));

        _logger.LogInformation("Processing queue entry: id={QueueEntryId} type={QueueEntryName} attempt={QueueEntryAttempt}", queueEntry.Id, _queueEntryName, queueEntry.Attempts);

        if (cancellationToken.IsCancellationRequested)
        {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Job was cancelled. Abandoning {QueueEntryName} queue entry: {Id}", _queueEntryName, queueEntry.Id);

            await queueEntry.AbandonAsync().AnyContext();
            return JobResult.CancelledWithMessage($"Abandoning {_queueEntryName} queue entry: {queueEntry.Id}");
        }

        var lockValue = await GetQueueEntryLockAsync(queueEntry, cancellationToken).AnyContext();
        if (lockValue is null)
        {
            await queueEntry.AbandonAsync().AnyContext();
            return JobResult.CancelledWithMessage($"Unable to acquire queue entry lock. Abandoning {_queueEntryName} queue entry: {queueEntry.Id}");
        }

        bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
        try
        {
            LogProcessingQueueEntry(queueEntry);
            var result = await ProcessQueueEntryAsync(new QueueEntryContext<T>(queueEntry, lockValue, cancellationToken)).AnyContext();

            if (!AutoComplete || queueEntry.IsCompleted || queueEntry.IsAbandoned)
                return result;

            if (result.IsSuccess)
            {
                await queueEntry.CompleteAsync().AnyContext();
                LogAutoCompletedQueueEntry(queueEntry);
            }
            else
            {
                if (result.Error != null || result.Message != null)
                    _logger.LogError(result.Error, "{QueueEntryName} queue entry {Id} returned an unsuccessful response: {Message}", _queueEntryName, queueEntry.Id, result.Message ?? result.Error?.Message);

                if (isTraceLogLevelEnabled)
                    _logger.LogTrace("Processing was not successful. Auto Abandoning {QueueEntryName} queue entry: {Id}", _queueEntryName, queueEntry.Id);
                await queueEntry.AbandonAsync().AnyContext();
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning("Auto abandoned {QueueEntryName} queue entry: {Id}", _queueEntryName, queueEntry.Id);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing {QueueEntryName} queue entry: {Id}", _queueEntryName, queueEntry.Id);

            if (!queueEntry.IsCompleted && !queueEntry.IsAbandoned)
                await queueEntry.AbandonAsync().AnyContext();

            throw;
        }
        finally
        {
            if (isTraceLogLevelEnabled)
                _logger.LogTrace("Releasing Lock for {QueueEntryName} queue entry: {Id}", _queueEntryName, queueEntry.Id);
            await lockValue.ReleaseAsync().AnyContext();
            if (isTraceLogLevelEnabled)
                _logger.LogTrace("Released Lock for {QueueEntryName} queue entry: {Id}", _queueEntryName, queueEntry.Id);
        }
    }

    protected virtual Activity StartProcessQueueEntryActivity(IQueueEntry<T> entry)
    {
        var activity = FoundatioDiagnostics.ActivitySource.StartActivity("ProcessQueueEntry", ActivityKind.Server, entry.CorrelationId);
        if (activity is null)
            return null;

        if (entry.Properties != null && entry.Properties.TryGetValue("TraceState", out var traceState))
            activity.TraceStateString = traceState.ToString();

        activity.DisplayName = $"Queue: {entry.EntryType.Name}";

        EnrichProcessQueueEntryActivity(activity, entry);

        return activity;
    }

    protected virtual void EnrichProcessQueueEntryActivity(Activity activity, IQueueEntry<T> entry)
    {
        if (!activity.IsAllDataRequested)
            return;

        activity.AddTag("EntryType", entry.EntryType.FullName);
        activity.AddTag("Id", entry.Id);
        activity.AddTag("CorrelationId", entry.CorrelationId);

        if (entry.Properties == null || entry.Properties.Count <= 0)
            return;

        foreach (var p in entry.Properties)
        {
            if (p.Key != "TraceState")
                activity.AddTag(p.Key, p.Value);
        }
    }

    protected virtual void LogProcessingQueueEntry(IQueueEntry<T> queueEntry)
    {
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Processing {QueueEntryName} queue entry: {Id}", _queueEntryName, queueEntry.Id);
    }

    protected virtual void LogAutoCompletedQueueEntry(IQueueEntry<T> queueEntry)
    {
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Auto completed {QueueEntryName} queue entry: {Id}", _queueEntryName, queueEntry.Id);
    }

    protected abstract Task<JobResult> ProcessQueueEntryAsync(QueueEntryContext<T> context);

    protected virtual Task<ILock> GetQueueEntryLockAsync(IQueueEntry<T> queueEntry, CancellationToken cancellationToken = default)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Returning Empty Lock for {QueueEntryName} queue entry: {Id}", _queueEntryName, queueEntry.Id);

        return Task.FromResult(Disposable.EmptyLock);
    }
}

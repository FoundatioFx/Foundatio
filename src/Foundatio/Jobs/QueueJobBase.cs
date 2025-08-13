using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Lock;
using Foundatio.Queues;
using Foundatio.Resilience;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Jobs;

public abstract class QueueJobBase<T> : IQueueJob<T>, IHaveLogger, IHaveLoggerFactory, IHaveTimeProvider, IHaveResiliencePolicyProvider where T : class
{
    protected readonly ILogger _logger;
    protected readonly ILoggerFactory _loggerFactory;
    protected readonly Lazy<IQueue<T>> _queue;
    protected readonly TimeProvider _timeProvider;
    protected readonly IResiliencePolicyProvider _resiliencePolicyProvider;
    protected readonly string _queueName = typeof(T).Name;

    public QueueJobBase(IQueue<T> queue, TimeProvider timeProvider = null, ILoggerFactory loggerFactory = null) : this(new Lazy<IQueue<T>>(() => queue), timeProvider, null, loggerFactory) { }

    public QueueJobBase(Lazy<IQueue<T>> queue, TimeProvider timeProvider = null, IResiliencePolicyProvider resiliencePolicyProvider = null, ILoggerFactory loggerFactory = null)
    {
        _queue = queue;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _resiliencePolicyProvider = resiliencePolicyProvider;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger(GetType());
        AutoComplete = true;
    }

    protected bool AutoComplete { get; set; }
    public string JobId { get; } = Guid.NewGuid().ToString("N").Substring(0, 10);
    IQueue<T> IQueueJob<T>.Queue => _queue.Value;
    ILogger IHaveLogger.Logger => _logger;
    ILoggerFactory IHaveLoggerFactory.LoggerFactory => _loggerFactory;
    TimeProvider IHaveTimeProvider.TimeProvider => _timeProvider;
    IResiliencePolicyProvider IHaveResiliencePolicyProvider.ResiliencePolicyProvider => _resiliencePolicyProvider;

    public virtual async Task<JobResult> RunAsync(CancellationToken cancellationToken = default)
    {
        IQueueEntry<T> queueEntry;

        using var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            using var dequeueActivity = StartDequeueActivity();
            queueEntry = await _queue.Value.DequeueAsync(linkedCancellationTokenSource.Token).AnyContext();
            EnrichDequeueActivity(dequeueActivity, queueEntry);
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
            return JobResult.SuccessWithMessage("No queue entry to process.");

        using var activity = StartProcessQueueEntryActivity(queueEntry);
        using var _ = _logger.BeginScope(s => s
            .Property("JobId", JobId)
            .Property("QueueName", _queueName)
            .Property("QueueEntryId", queueEntry.Id)
            .PropertyIf("CorrelationId", queueEntry.CorrelationId, !String.IsNullOrEmpty(queueEntry.CorrelationId)));

        _logger.LogInformation("Processing queue entry: id={QueueEntryId} type={QueueName} attempt={QueueEntryAttempt}", queueEntry.Id, _queueName, queueEntry.Attempts);

        if (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Job was cancelled. Abandoning {QueueName} queue entry: {QueueEntryId}", _queueName, queueEntry.Id);

            await queueEntry.AbandonAsync().AnyContext();
            return JobResult.CancelledWithMessage($"Abandoning {_queueName} queue entry: {queueEntry.Id}");
        }

        var lockValue = await GetQueueEntryLockAsync(queueEntry, cancellationToken).AnyContext();
        if (lockValue is null)
        {
            await queueEntry.AbandonAsync().AnyContext();
            return JobResult.CancelledWithMessage($"Unable to acquire queue entry lock. Abandoning {_queueName} queue entry: {queueEntry.Id}");
        }

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
                    _logger.LogError(result.Error, "{QueueName} queue entry {QueueEntryId} returned an unsuccessful response: {Message}", _queueName, queueEntry.Id, result.Message ?? result.Error?.Message);

                _logger.LogTrace("Processing was not successful. Auto Abandoning {QueueName} queue entry: {QueueEntryId}", _queueName, queueEntry.Id);
                await queueEntry.AbandonAsync().AnyContext();
                _logger.LogWarning("Auto abandoned {QueueName} queue entry: {QueueEntryId}", _queueName, queueEntry.Id);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing {QueueName} queue entry: {QueueEntryId}", _queueName, queueEntry.Id);

            if (!queueEntry.IsCompleted && !queueEntry.IsAbandoned)
                await queueEntry.AbandonAsync().AnyContext();

            throw;
        }
        finally
        {
            _logger.LogTrace("Releasing Lock for {QueueName} queue entry: {QueueEntryId}", _queueName, queueEntry.Id);
            await lockValue.ReleaseAsync().AnyContext();
            _logger.LogTrace("Released Lock for {QueueName} queue entry: {QueueEntryId}", _queueName, queueEntry.Id);
        }
    }

    protected virtual Activity StartDequeueActivity()
    {
        var activity = FoundatioDiagnostics.ActivitySource.StartActivity("DequeueQueueEntry");
        if (activity is null)
            return null;

        activity.DisplayName = $"Dequeue: {_queueName}";
        activity.AddTag("QueueName", _queueName);
        activity.AddTag("JobId", JobId);

        return activity;
    }

    protected virtual void EnrichDequeueActivity(Activity activity, IQueueEntry<T> entry)
    {
        if (activity is null || !activity.IsAllDataRequested)
            return;

        if (entry is null)
            return;

        activity.AddTag("EntryType", entry.EntryType.FullName);
        activity.AddTag("Id", entry.Id);
        activity.AddTag("CorrelationId", entry.CorrelationId);
    }

    protected virtual Activity StartProcessQueueEntryActivity(IQueueEntry<T> entry)
    {
        var activity = FoundatioDiagnostics.ActivitySource.StartActivity("ProcessQueueEntry", ActivityKind.Internal, entry.CorrelationId);
        if (activity is null)
            return null;

        if (entry.Properties != null && entry.Properties.TryGetValue("TraceState", out string traceState))
            activity.TraceStateString = traceState;

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

        if (entry.Properties is not { Count: > 0 })
            return;

        foreach (var p in entry.Properties)
        {
            if (p.Key != "TraceState")
                activity.AddTag(p.Key, p.Value);
        }
    }

    protected virtual void LogProcessingQueueEntry(IQueueEntry<T> queueEntry)
    {
        _logger.LogInformation("Processing {QueueName} queue entry: {QueueEntryId}", _queueName, queueEntry.Id);
    }

    protected virtual void LogAutoCompletedQueueEntry(IQueueEntry<T> queueEntry)
    {
        _logger.LogInformation("Auto completed {QueueName} queue entry: {QueueEntryId}", _queueName, queueEntry.Id);
    }

    protected abstract Task<JobResult> ProcessQueueEntryAsync(QueueEntryContext<T> context);

    protected virtual Task<ILock> GetQueueEntryLockAsync(IQueueEntry<T> queueEntry, CancellationToken cancellationToken = default)
    {
        _logger.LogTrace("Returning Empty Lock for {QueueName} queue entry: {QueueEntryId}", _queueName, queueEntry.Id);

        return Task.FromResult(Disposable.EmptyLock);
    }
}

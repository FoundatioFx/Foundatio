using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Messaging;

/// <summary>
/// Executes broker deliveries with optional job progress, cancellation, and history.
/// The broker owns delivery leases and retries; the job store records each attempt without scheduling it.
/// </summary>
public sealed class MessageExecutionPipeline
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);
    private readonly MessageExecutionOptions _options;
    private readonly IJobRuntimeStore? _store;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public MessageExecutionPipeline(MessageExecutionOptions options, IJobRuntimeStore? store = null, TimeProvider? timeProvider = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.QueueName);
        ArgumentOutOfRangeException.ThrowIfEqual(options.MaxAttempts, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.CancellationPollInterval, TimeSpan.Zero);
        if (options.TrackProgress && store is null)
            throw new ArgumentException("Execution tracking requires a job store. Configure AddFoundatio().Jobs.UseInMemory() or Jobs.UseRedis().", nameof(store));
        _options = options;
        _store = store;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Processes a delivery, recording terminal job state only after confirmed broker settlement.</summary>
    public async Task ProcessAsync(IMessageContext delivery, Func<MessageProcessingContext, CancellationToken, ValueTask<MessageOutcome>> handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(handler);
        string? jobId = _options.TrackProgress ? delivery.Headers.GetValueOrDefault(_options.ExecutionIdHeader) : null;
        JobState? attempt = null;
        if (jobId is not null)
        {
            attempt = await RunAsync(ct => _store!.BeginBrokerAttemptAsync(jobId, delivery.Attempts, _options.WorkerId, ct), cancellationToken).AnyContext();
            if (attempt is null)
            {
                var state = await RunAsync(ct => _store!.GetAsync(jobId, ct), cancellationToken).AnyContext();
                if (state is not null)
                {
                    if (state.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled or JobStatus.DeadLettered)
                        await SettleAsync(delivery.CompleteAsync, delivery).AnyContext();
                    else
                        await SettleAsync(ct => delivery.RejectAsync(new RejectOptions { RedeliveryDelay = _options.RetryBackoff(delivery.Attempts) }, ct), delivery).AnyContext();
                    return;
                }
                _logger.LogWarning("Job history {JobId} expired before delivery; processing continues without retained history", jobId);
            }
        }

        using var processing = CancellationTokenSource.CreateLinkedTokenSource(delivery.CancellationToken, cancellationToken);
        var token = processing.Token;
        long started = Stopwatch.GetTimestamp();
        Task? poll = attempt is null ? null : PollCancellationAsync(attempt, processing);
        var context = new MessageProcessingContext
        {
            Body = delivery.Body,
            QueueName = _options.QueueName,
            MessageId = delivery.BrokerMessageId,
            MessageType = _options.MessageType,
            DequeueCount = delivery.Attempts,
            MaxAttempts = _options.MaxAttempts,
            VisibilityTimeout = _options.VisibilityTimeout,
            EnqueuedAt = delivery.EnqueuedUtc ?? _time.GetUtcNow(),
            JobId = jobId,
            Headers = delivery.Headers,
            OnCancelProcessing = processing.Cancel,
            OnRenewTimeout = (duration, ct) => delivery.RenewLockAsync(duration, ct),
            OnComplete = ct => RunAsync(delivery.CompleteAsync, ct),
            OnAbandon = (delay, ct) => RunAsync(t => delivery.RejectAsync(new RejectOptions { RedeliveryDelay = delay }, t), ct),
            OnReportProgress = async ct =>
            {
                await delivery.RenewLockAsync(_options.VisibilityTimeout, ct).AnyContext();
                if (attempt is not null)
                    await RecordAsync(t => _store!.HeartbeatJobAsync(attempt.JobId, attempt.ClaimToken!, t), ct).AnyContext();
            },
            OnReportDetailedProgress = attempt is null ? null : async (percent, message, ct) =>
            {
                if (await IsCancelledAsync(attempt.JobId, ct).AnyContext())
                    throw new OperationCanceledException("Job cancellation was requested.");
                await RecordAsync(t => _store!.ReportJobProgressAsync(attempt.JobId, attempt.ClaimToken!, Math.Clamp(percent, 0, 100), message, t), ct).AnyContext();
            }
        };
        JobCompletion completion = new() { Kind = JobCompletionKind.Interrupted };
        try
        {
            if (attempt?.CancellationRequested == true)
            {
                if (await SettleAsync(delivery.CompleteAsync, delivery).AnyContext())
                    completion = new() { Kind = JobCompletionKind.Cancelled };
            }
            else if (_options.MaxAttempts > 0 && delivery.Attempts > _options.MaxAttempts)
                completion = await DeadLetterAsync(delivery, $"Exceeded max attempts ({_options.MaxAttempts})").AnyContext();
            else
            {
                var outcome = await handler(context, token).AnyContext();
                if (!context.IsCompleted && !context.IsAbandoned)
                {
                    if (outcome.Kind == MessageOutcomeKind.Retry)
                        completion = await FailureAsync(delivery, outcome.Reason ?? "Processing failed").AnyContext();
                    else if (outcome.Kind == MessageOutcomeKind.DeadLetter)
                        completion = await DeadLetterAsync(delivery, outcome.Reason ?? "Processing rejected").AnyContext();
                    else if (_options.AutoComplete && outcome.Kind != MessageOutcomeKind.Unsettled)
                    {
                        token.ThrowIfCancellationRequested();
                        await SettleAsync(context.CompleteAsync, delivery).AnyContext();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (delivery.IsLeaseLost) { }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!context.IsCompleted && !context.IsAbandoned)
                await SettleAsync(ct => delivery.RejectAsync(new RejectOptions { RedeliveryDelay = TimeSpan.Zero }, ct), delivery).AnyContext();
        }
        catch (OperationCanceledException)
        {
            if (!context.IsCompleted && !context.IsAbandoned)
            {
                if (attempt is not null && await IsCancelledAsync(attempt.JobId, CancellationToken.None).AnyContext())
                {
                    if (await SettleAsync(delivery.CompleteAsync, delivery).AnyContext())
                        completion = new() { Kind = JobCompletionKind.Cancelled };
                }
                else completion = await FailureAsync(delivery, "Processing was cancelled").AnyContext();
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Processing failed for {MessageId} at {Queue} on attempt {Attempt}", delivery.Id, _options.QueueName, delivery.Attempts);
            if (!delivery.IsLeaseLost && !context.IsCompleted && !context.IsAbandoned)
                completion = await FailureAsync(delivery, exception.Message).AnyContext();
        }
        finally
        {
            await processing.CancelAsync().AnyContext();
            if (poll is not null) await poll.AnyContext();
            if (context.IsCompleted) completion = new() { Kind = JobCompletionKind.Succeeded };
            if (attempt is not null)
                await RecordAsync(ct => _store!.CompleteJobAsync(attempt.JobId, attempt.ClaimToken!, completion, ct)).AnyContext();
            var outcome = completion.Kind switch
            {
                JobCompletionKind.Succeeded => MessageOutcomeKind.Success,
                JobCompletionKind.Failed when !completion.Retryable => MessageOutcomeKind.DeadLetter,
                JobCompletionKind.Failed => MessageOutcomeKind.Retry,
                _ => (MessageOutcomeKind?)null
            };
            if (outcome is { } kind)
            {
                _options.OnProcessed?.Invoke(kind, Stopwatch.GetElapsedTime(started));
                if (_store is not null)
                    await RecordAsync(ct => _store.IncrementCounterAsync(_options.QueueName, kind == MessageOutcomeKind.Success ? "processed" : kind == MessageOutcomeKind.DeadLetter ? "dead_lettered" : "failed", 1, ct)).AnyContext();
            }
        }
    }

    private async Task<JobCompletion> FailureAsync(IMessageContext delivery, string reason)
    {
        if (_options.AutoComplete && _options.MaxAttempts > 0 && delivery.Attempts >= _options.MaxAttempts)
            return await DeadLetterAsync(delivery, reason).AnyContext();
        if (_options.AutoComplete)
            await SettleAsync(ct => delivery.RejectAsync(new RejectOptions { RedeliveryDelay = _options.RetryBackoff(delivery.Attempts) }, ct), delivery).AnyContext();
        return new() { Kind = JobCompletionKind.Failed, Error = reason };
    }

    private async Task<JobCompletion> DeadLetterAsync(IMessageContext delivery, string reason)
        => await SettleAsync(ct => MessageOutcome.DeadLetter(reason).SettleFailureAsync(delivery, _options.MaxAttempts, _options.RetryBackoff, ct), delivery).AnyContext()
            ? new() { Kind = JobCompletionKind.Failed, Retryable = false, Error = reason }
            : new() { Kind = JobCompletionKind.Interrupted, Error = reason };

    private Task<bool> IsCancelledAsync(string jobId, CancellationToken token) => RunAsync(ct => _store!.IsCancellationRequestedAsync(jobId, ct), token);

    private async Task PollCancellationAsync(JobState attempt, CancellationTokenSource processing)
    {
        var token = processing.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.CancellationPollInterval, _time, token).AnyContext();
                if (await IsCancelledAsync(attempt.JobId, token).AnyContext())
                {
                    await processing.CancelAsync().AnyContext();
                    return;
                }
                await RecordAsync(ct => _store!.HeartbeatJobAsync(attempt.JobId, attempt.ClaimToken!, ct), token).AnyContext();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception exception) { _logger.LogWarning(exception, "Unable to poll job {JobId}; retrying", attempt.JobId); }
        }
    }

    private async Task<bool> SettleAsync(Func<CancellationToken, Task> operation, IMessageContext delivery)
    {
        try { await RunAsync(operation).AnyContext(); return true; }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Settlement was not confirmed for {MessageId} at {Queue}; delivery may recur", delivery.Id, _options.QueueName);
            return false;
        }
    }

    private Task RecordAsync(Func<CancellationToken, Task<bool>> operation, CancellationToken cancellationToken = default)
        => RecordAsync(async ct => { _ = await operation(ct).AnyContext(); }, cancellationToken);

    private async Task RecordAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
    {
        try { await RunAsync(operation, cancellationToken).AnyContext(); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { _logger.LogWarning(exception, "Unable to persist job history for {Queue}", _options.QueueName); }
    }

    private async Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
    {
        using var deadline = new CancellationTokenSource(OperationTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        await operation(linked.Token).WaitAsync(linked.Token).AnyContext();
    }

    private async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        using var deadline = new CancellationTokenSource(OperationTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        return await operation(linked.Token).WaitAsync(linked.Token).AnyContext();
    }
}

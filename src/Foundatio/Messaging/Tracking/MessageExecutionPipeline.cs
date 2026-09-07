using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Messaging;

/// <summary>
/// Executes a broker delivery with optional progress, cancellation, history, and per-attempt state fencing.
/// The message bus remains the sole owner of receiving and delivery leases; this pipeline creates no runnable jobs.
/// </summary>
public sealed class MessageExecutionPipeline
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);
    private readonly MessageExecutionOptions _options;
    private readonly IMessageExecutionStore? _store;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public MessageExecutionPipeline(MessageExecutionOptions options, IMessageExecutionStore? store = null, TimeProvider? timeProvider = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.QueueName);
        ArgumentOutOfRangeException.ThrowIfEqual(options.MaxAttempts, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.CancellationPollInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.StateRetention, TimeSpan.Zero);
        if (options.TrackProgress && store is null)
            throw new ArgumentException("Execution tracking requires an IMessageExecutionStore.", nameof(store));
        _options = options;
        _store = store;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Runs application processing and persists only confirmed settlement outcomes.</summary>
    public async Task ProcessAsync(IMessageContext delivery, Func<MessageProcessingContext, CancellationToken, ValueTask<MessageOutcome>> handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(handler);
        string? jobId = _options.TrackProgress && _store is not null ? delivery.Headers.GetValueOrDefault(_options.ExecutionIdHeader) : null;
        if (_options.MaxAttempts >= 0 && delivery.Attempts > _options.MaxAttempts)
        {
            await DeadLetterAsync(delivery, jobId, $"Exceeded max attempts ({_options.MaxAttempts})").AnyContext();
            return;
        }

        using var processing = CancellationTokenSource.CreateLinkedTokenSource(delivery.CancellationToken, cancellationToken);
        var token = processing.Token;
        Task? poll = null;
        long started = Stopwatch.GetTimestamp();
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
                if (jobId is not null)
                    await UpdateAsync(t => _store!.HeartbeatAsync(jobId, t, delivery.Attempts, _options.StateRetention), ct).AnyContext();
            },
            OnReportDetailedProgress = jobId is null ? null : async (percent, message, ct) =>
            {
                if (await IsCancelledAsync(jobId, ct).AnyContext())
                    throw new OperationCanceledException("Job cancellation was requested.");
                await UpdateAsync(t => _store!.UpdateJobProgressAsync(jobId, Math.Clamp(percent, 0, 100), message, _options.StateRetention, t, delivery.Attempts), ct).AnyContext();
                await UpdateAsync(t => _store!.HeartbeatAsync(jobId, t, delivery.Attempts, _options.StateRetention), ct).AnyContext();
            }
        };
        try
        {
            if (jobId is not null)
            {
                if (await IsCancelledAsync(jobId, token).AnyContext())
                {
                    if (await SettleAsync(delivery.CompleteAsync, delivery).AnyContext())
                        await StatusAsync(jobId, MessageExecutionStatus.Cancelled, delivery.Attempts).AnyContext();
                    return;
                }
                poll = PollCancellationAsync(jobId, delivery.Attempts, processing);
                bool accepted = await RunAsync(ct => _store!.UpdateJobStatusAsync(jobId, MessageExecutionStatus.Processing,
                    startedUtc: _time.GetUtcNow(), attempt: delivery.Attempts, expiry: _options.StateRetention, cancellationToken: ct, workerId: _options.WorkerId), token).AnyContext();
                if (!accepted)
                {
                    var state = await RunAsync(ct => _store!.GetJobStateAsync(jobId, ct), token).AnyContext();
                    if (state?.Status is MessageExecutionStatus.Completed or MessageExecutionStatus.Failed or MessageExecutionStatus.Cancelled)
                        await SettleAsync(delivery.CompleteAsync, delivery).AnyContext();
                    else if (state is not null)
                        await RetryAsync(delivery).AnyContext();
                    // Tracking retention must not become a second delivery scheduler. Expired history
                    // does not prevent broker-owned work from running; state updates become no-ops.
                    if (state is not null) return;
                    _logger.LogWarning("Execution history {JobId} expired before delivery; processing continues without retained history", jobId);
                }
            }

            var outcome = await handler(context, token).AnyContext();
            if (!context.IsCompleted && !context.IsAbandoned)
            {
                if (outcome.Kind == MessageOutcomeKind.Retry)
                {
                    await FailureAsync(delivery, jobId, outcome.Reason ?? "Processing failed", Stopwatch.GetElapsedTime(started)).AnyContext();
                    return;
                }
                if (outcome.Kind == MessageOutcomeKind.DeadLetter)
                {
                    await DeadLetterAsync(delivery, jobId, outcome.Reason ?? "Processing rejected").AnyContext();
                    return;
                }
            }
            if (context.IsAbandoned) return;
            token.ThrowIfCancellationRequested();
            if (_options.AutoComplete && !context.IsCompleted && outcome.Kind != MessageOutcomeKind.Unsettled)
                await SettleAsync(context.CompleteAsync, delivery).AnyContext();
            if (!context.IsCompleted)
                await StatusAsync(jobId, MessageExecutionStatus.RetryPending, delivery.Attempts, "Handler finished without confirmed acknowledgment; delivery may recur.").AnyContext();
        }
        catch (OperationCanceledException) when (delivery.IsLeaseLost) { }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!context.IsCompleted && !context.IsAbandoned)
            {
                await SettleAsync(ct => delivery.RejectAsync(new RejectOptions { RedeliveryDelay = TimeSpan.Zero }, ct), delivery).AnyContext();
                await StatusAsync(jobId, MessageExecutionStatus.RetryPending, delivery.Attempts).AnyContext();
            }
        }
        catch (OperationCanceledException)
        {
            if (context.IsCompleted || context.IsAbandoned) return;
            if (jobId is not null && await IsCancelledAsync(jobId, CancellationToken.None).AnyContext())
            {
                if (await SettleAsync(delivery.CompleteAsync, delivery).AnyContext())
                    await StatusAsync(jobId, MessageExecutionStatus.Cancelled, delivery.Attempts).AnyContext();
            }
            else
                await FailureAsync(delivery, jobId, "Processing was cancelled", Stopwatch.GetElapsedTime(started)).AnyContext();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Processing failed for {MessageId} at {Queue} on attempt {Attempt}", delivery.Id, _options.QueueName, delivery.Attempts);
            if (!delivery.IsLeaseLost && !context.IsCompleted && !context.IsAbandoned)
                await FailureAsync(delivery, jobId, exception.Message, Stopwatch.GetElapsedTime(started)).AnyContext();
        }
        finally
        {
            await processing.CancelAsync().AnyContext();
            if (poll is not null) await poll.AnyContext();
            if (context.IsCompleted)
            {
                _options.OnProcessed?.Invoke(MessageOutcomeKind.Success, Stopwatch.GetElapsedTime(started));
                if (jobId is not null)
                    await UpdateAsync(ct => _store!.UpdateJobStatusAsync(jobId, MessageExecutionStatus.Completed, attempt: delivery.Attempts,
                        completedUtc: _time.GetUtcNow(), progress: 100, expiry: _options.StateRetention, cancellationToken: ct)).AnyContext();
                await CounterAsync("processed").AnyContext();
            }
            else if (context.IsAbandoned)
                await StatusAsync(jobId, MessageExecutionStatus.RetryPending, delivery.Attempts).AnyContext();
        }
    }

    private async Task FailureAsync(IMessageContext delivery, string? jobId, string reason, TimeSpan elapsed)
    {
        if (_options.AutoComplete && _options.MaxAttempts > 0 && delivery.Attempts >= _options.MaxAttempts)
            await DeadLetterAsync(delivery, jobId, reason).AnyContext();
        else
        {
            if (_options.AutoComplete) await RetryAsync(delivery).AnyContext();
            await StatusAsync(jobId, MessageExecutionStatus.RetryPending, delivery.Attempts, reason).AnyContext();
        }
        _options.OnProcessed?.Invoke(MessageOutcomeKind.Retry, elapsed);
        await CounterAsync("failed").AnyContext();
    }

    private async Task DeadLetterAsync(IMessageContext delivery, string? jobId, string reason)
    {
        if (!await SettleAsync(ct => MessageOutcome.DeadLetter(reason).SettleFailureAsync(delivery, _options.MaxAttempts, _options.RetryBackoff, ct), delivery).AnyContext())
        {
            await StatusAsync(jobId, MessageExecutionStatus.RetryPending, delivery.Attempts, reason).AnyContext();
            return;
        }
        _options.OnProcessed?.Invoke(MessageOutcomeKind.DeadLetter, TimeSpan.Zero);
        await StatusAsync(jobId, MessageExecutionStatus.Failed, delivery.Attempts, reason).AnyContext();
        await CounterAsync("dead_lettered").AnyContext();
    }

    private Task<bool> RetryAsync(IMessageContext delivery) => SettleAsync(ct => delivery.RejectAsync(new RejectOptions { RedeliveryDelay = _options.RetryBackoff(delivery.Attempts) }, ct), delivery);
    private Task<bool> IsCancelledAsync(string jobId, CancellationToken token) => RunAsync(ct => _store!.IsCancellationRequestedAsync(jobId, ct), token);
    private Task CounterAsync(string name) => _store is null ? Task.CompletedTask : UpdateAsync(ct => _store.IncrementCounterAsync(_options.QueueName, name, 1, ct));
    private Task StatusAsync(string? jobId, MessageExecutionStatus status, int attempt, string? error = null)
        => jobId is null ? Task.CompletedTask : UpdateAsync(ct => _store!.UpdateJobStatusAsync(jobId, status, attempt: attempt,
            completedUtc: status is MessageExecutionStatus.Completed or MessageExecutionStatus.Failed or MessageExecutionStatus.Cancelled ? _time.GetUtcNow() : null,
            errorMessage: error, expiry: _options.StateRetention, cancellationToken: ct));

    private async Task PollCancellationAsync(string jobId, int attempt, CancellationTokenSource processing)
    {
        var token = processing.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.CancellationPollInterval, _time, token).AnyContext();
                if (await IsCancelledAsync(jobId, token).AnyContext())
                {
                    await processing.CancelAsync().AnyContext();
                    return;
                }
                await UpdateAsync(ct => _store!.HeartbeatAsync(jobId, ct, attempt, _options.StateRetention), token).AnyContext();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception exception) { _logger.LogWarning(exception, "Unable to poll execution {JobId}; retrying", jobId); }
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
    private Task UpdateAsync(Func<CancellationToken, Task<bool>> operation, CancellationToken cancellationToken = default)
        => UpdateAsync(async ct => { _ = await operation(ct).AnyContext(); }, cancellationToken);

    private async Task UpdateAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
    {
        try { await RunAsync(operation, cancellationToken).AnyContext(); }
        catch (Exception exception) { _logger.LogWarning(exception, "Unable to update execution state at {Queue}", _options.QueueName); }
    }
    private async Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
    {
        using var timeout = new CancellationTokenSource(OperationTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        await operation(linked.Token).WaitAsync(linked.Token).AnyContext();
    }
    private async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        using var timeout = new CancellationTokenSource(OperationTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        return await operation(linked.Token).WaitAsync(linked.Token).AnyContext();
    }
}

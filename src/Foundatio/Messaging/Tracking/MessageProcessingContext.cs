using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace Foundatio.Messaging;

/// <summary>Execution progress, cancellation and explicit settlement for a broker-delivered message.</summary>
public class MessageProcessingContext
{
    private SemaphoreSlim? _settlementGate;
    private int _settlement;

    /// <summary>The serialized application payload.</summary>
    public ReadOnlyMemory<byte> Body { get; init; }

    internal Action? OnSettled { get; init; }
    internal Action? OnCancelProcessing { get; init; }

    /// <summary>Cancel cooperative processing after losing an application resource lock.</summary>
    public void CancelProcessing() => OnCancelProcessing?.Invoke();

    /// <summary>
    /// The name of the queue this message was received from.
    /// </summary>
    public string QueueName { get; init; } = string.Empty;

    /// <summary>
    /// The transport-assigned id of the message being processed.
    /// </summary>
    public string MessageId { get; init; } = string.Empty;

    /// <summary>
    /// The visibility timeout the worker requested for this message.
    /// </summary>
    public TimeSpan VisibilityTimeout { get; init; }

    /// <summary>
    /// The message type being processed.
    /// </summary>
    public Type? MessageType { get; init; }

    /// <summary>
    /// The number of times this message has been dequeued (including the current attempt).
    /// Useful for detecting poison messages or implementing backoff strategies.
    /// </summary>
    public int DequeueCount { get; init; }

    /// <summary>
    /// The maximum number of attempts configured for this queue.
    /// After this many attempts, the message will be dead-lettered.
    /// </summary>
    public int MaxAttempts { get; init; }

    /// <summary>
    /// When the message was originally enqueued.
    /// </summary>
    public DateTimeOffset EnqueuedAt { get; init; }

    /// <summary>
    /// The unique job identifier for progress tracking, or <c>null</c> if tracking is not enabled.
    /// </summary>
    public string? JobId { get; init; }

    /// <summary>Message headers, including correlation, propagated context, and replay lineage.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = MessageHeaders.Empty;

    /// <summary>
    /// Delegate invoked by <see cref="ReportProgressAsync(CancellationToken)"/> to signal that the handler
    /// is still actively working. This acts as a heartbeat keep-alive that extends the
    /// message visibility by the configured timeout. Set by the worker infrastructure.
    /// </summary>
    internal Func<CancellationToken, Task>? OnReportProgress { get; init; }

    /// <summary>
    /// Delegate invoked by <see cref="ReportProgressAsync(int, string?, CancellationToken)"/>
    /// to update progress percentage and message in the state store.
    /// Set by the worker infrastructure when progress tracking is enabled.
    /// </summary>
    internal Func<int, string?, CancellationToken, Task>? OnReportDetailedProgress { get; init; }

    /// <summary>
    /// Delegate invoked by <see cref="RenewTimeoutAsync"/> to extend the message lock
    /// or visibility timeout by a specific duration. Set by the worker infrastructure.
    /// </summary>
    internal Func<TimeSpan, CancellationToken, Task>? OnRenewTimeout { get; init; }

    /// <summary>
    /// Delegate invoked by <see cref="CompleteAsync"/> to remove the message from the queue.
    /// Set by the worker infrastructure.
    /// </summary>
    internal Func<CancellationToken, Task>? OnComplete { get; init; }

    /// <summary>
    /// Delegate invoked by <see cref="AbandonAsync(TimeSpan, CancellationToken)"/> to
    /// return the message to the queue for redelivery. Set by the worker infrastructure.
    /// </summary>
    internal Func<TimeSpan, CancellationToken, Task>? OnAbandon { get; init; }

    /// <summary>
    /// Indicates whether the handler explicitly completed the message via <see cref="CompleteAsync"/>.
    /// When true, the worker infrastructure will skip automatic completion.
    /// </summary>
    public bool IsCompleted => Volatile.Read(ref _settlement) == 1;

    /// <summary>
    /// Indicates whether the handler explicitly abandoned the message via <see cref="AbandonAsync(CancellationToken)"/>
    /// or <see cref="AbandonAsync(TimeSpan, CancellationToken)"/>.
    /// When true, the worker infrastructure will skip automatic abandonment.
    /// </summary>
    public bool IsAbandoned => Volatile.Read(ref _settlement) == 2;

    /// <summary>
    /// Reports that the handler is still actively processing the message.
    /// For transports that support it, this extends the visibility timeout
    /// by the configured default duration, preventing the message from being
    /// redelivered during long-running operations.
    /// </summary>
    public Task ReportProgressAsync(CancellationToken cancellationToken = default)
        => OnReportProgress?.Invoke(cancellationToken) ?? Task.CompletedTask;

    /// <summary>
    /// Reports progress with a percentage and optional message.
    /// When progress tracking is enabled, this updates the job state store
    /// and checks for cancellation. If cancellation has been requested,
    /// an <see cref="OperationCanceledException"/> is thrown.
    /// Also acts as a heartbeat to extend the message visibility timeout.
    /// </summary>
    /// <param name="progressPercent">Progress percentage (0–100).</param>
    /// <param name="message">Optional description of current work.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public async Task ReportProgressAsync(int progressPercent, string? message = null, CancellationToken cancellationToken = default)
    {
        // Always renew the visibility timeout as a heartbeat
        if (OnReportProgress is not null)
            await OnReportProgress(cancellationToken).ConfigureAwait(false);

        // Update state store and check for cancellation
        if (OnReportDetailedProgress is not null)
            await OnReportDetailedProgress(progressPercent, message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Extends the message lock or visibility timeout by the specified duration.
    /// Use this for long-running handlers to prevent the message from being
    /// redelivered to another consumer.
    /// </summary>
    public Task RenewTimeoutAsync(TimeSpan extension, CancellationToken cancellationToken = default)
        => OnRenewTimeout?.Invoke(extension, cancellationToken) ?? Task.CompletedTask;

    /// <summary>
    /// Completes the message, removing it from the queue permanently.
    /// Use this when <c>AutoComplete</c> is disabled and the handler has finished
    /// processing successfully. If <c>AutoComplete</c> is enabled, the worker
    /// infrastructure will skip its own completion when this has been called.
    /// </summary>
    public Task CompleteAsync(CancellationToken cancellationToken = default)
        => SettleAsync(1, OnComplete, cancellationToken);

    private async Task SettleAsync(int outcome, Func<CancellationToken, Task>? operation, CancellationToken cancellationToken)
    {
        var gate = LazyInitializer.EnsureInitialized(ref _settlementGate, static () => new SemaphoreSlim(1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_settlement == outcome)
                return;
            if (_settlement != 0)
                throw new InvalidOperationException("This delivery has already been settled with a different outcome.");
            if (operation is not null)
                await operation(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _settlement, outcome);
            OnSettled?.Invoke();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Abandons the message so it becomes immediately visible for redelivery.
    /// Use this when <c>AutoComplete</c> is disabled and the handler cannot
    /// process the message successfully.
    /// </summary>
    public Task AbandonAsync(CancellationToken cancellationToken = default)
        => AbandonAsync(TimeSpan.Zero, cancellationToken);

    /// <summary>
    /// Abandons the message so it becomes visible for redelivery after the specified delay.
    /// Use this when <c>AutoComplete</c> is disabled and the handler wants to retry
    /// the message after a backoff period.
    /// </summary>
    /// <param name="delay">How long before the message becomes visible again. Use <see cref="TimeSpan.Zero"/> for immediate redelivery.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public Task AbandonAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        => SettleAsync(2, ct => OnAbandon?.Invoke(delay, ct) ?? Task.CompletedTask, cancellationToken);
}

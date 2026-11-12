using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging.Transport;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging;

/// <summary>
/// Receives from one source with bounded concurrency, keeps every delivery's lease renewed until it is settled,
/// replaces a failed receiver and drains on shutdown. What happens to each delivery is up to its <see cref="IMessageDeliveryHandler"/>.
/// </summary>
internal sealed class MessageProcessor : IAsyncDisposable
{
    // Long enough to be one SQS long poll; stopping cancels a receive in progress, so shutdown does not wait for it.
    private static readonly TimeSpan ReceiveWait = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan AbortGracePeriod = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxReceiveBackoff = TimeSpan.FromSeconds(30);
    private const int ReplaceReceiverAfterFailures = 5;

    private readonly IMessageTransport _transport;
    private readonly MessageProcessorSettings _settings;
    private readonly IMessageDeliveryHandler _handler;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly ReceiverOptions _receiverOptions;
    private readonly LeaseRenewer _renewer;
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _stopReceiving = new();
    private readonly CancellationTokenSource _abortHandlers = new();
    private readonly object _lifecycleLock = new();
    private IMessageReceiver? _receiver;
    private Task _receiveLoop = Task.CompletedTask;
    private int _inFlight;
    private bool _started;
    private bool _stopped;

    public MessageProcessor(IMessageTransport transport, MessageSource source, MessageProcessorSettings settings, IMessageDeliveryHandler handler,
        ProcessingStatusTracker status, TimeProvider timeProvider, ILoggerFactory loggerFactory)
    {
        _transport = transport;
        Source = source;
        _settings = settings;
        _handler = handler;
        Status = status;
        _timeProvider = timeProvider;
        _logger = loggerFactory.CreateLogger<MessageProcessor>();

        var capabilities = transport.Capabilities;
        var lease = capabilities.MaxLeaseDuration is { } maxLease && settings.LeaseDuration > maxLease ? maxLease : settings.LeaseDuration;
        _receiverOptions = new ReceiverOptions { LeaseDuration = lease, PrefetchCount = settings.MaxConcurrency };
        _renewer = new LeaseRenewer(lease, capabilities.LeaseExpiration, timeProvider, _logger);
        _slots = new SemaphoreSlim(settings.MaxConcurrency, settings.MaxConcurrency);
    }

    public MessageSource Source { get; }

    /// <summary>Read by health checks. Receive errors are recorded here; they never stop the processor.</summary>
    public ProcessingStatusTracker Status { get; }

    public int InFlight => Volatile.Read(ref _inFlight);

    public ProcessingStatus GetStatus() => Status.GetStatus(Source.ToString(), InFlight);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var receiver = await _transport.CreateReceiverAsync(Source, _receiverOptions, cancellationToken).AnyContext();

        // Checked and assigned under the lock StopAsync takes, so a concurrent stop always sees the receiver it must close.
        bool started = false;
        lock (_lifecycleLock)
        {
            if (!_stopped && !_started)
            {
                _started = started = true;
                _receiver = receiver;
                Status.MarkRunning();
                _receiveLoop = Task.Run(RunReceiveLoopAsync, CancellationToken.None);
            }
        }

        if (!started)
        {
            await receiver.DisposeAsync().AnyContext();
            return;
        }

        _logger.LogInformation("Receiving {Source} with {Handlers} (concurrency {MaxConcurrency})", Source, _settings.Description, _settings.MaxConcurrency);
    }

    /// <summary>
    /// Stops receiving and waits for in-flight messages to finish until <paramref name="drainTimeout"/> elapses or
    /// <paramref name="cancellationToken"/> is cancelled. Handlers still running are then cancelled and their messages released.
    /// </summary>
    public async Task StopAsync(TimeSpan drainTimeout, CancellationToken cancellationToken = default)
    {
        Task receiveLoop;
        lock (_lifecycleLock)
        {
            if (_stopped)
                return;

            _stopped = true;
            receiveLoop = _receiveLoop;
        }

        Status.MarkStopped();
        _stopReceiving.Cancel();
        await receiveLoop.AnyContext();

        if (!await WaitForInFlightAsync(drainTimeout, cancellationToken).AnyContext())
        {
            _logger.LogWarning("Cancelling {Count} in-flight messages from {Source} at shutdown; they will be delivered again", Volatile.Read(ref _inFlight), Source);
            _abortHandlers.Cancel();
            await WaitForInFlightAsync(AbortGracePeriod, CancellationToken.None).AnyContext();
        }

        _renewer.Dispose();
        if (_receiver is { } receiver)
        {
            try
            {
                await receiver.DisposeAsync().AnyContext();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error closing receiver for {Source}", Source);
            }
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync(TimeSpan.Zero));

    private async Task RunReceiveLoopAsync()
    {
        try
        {
            await ReceiveLoopAsync().AnyContext();
        }
        catch (Exception ex)
        {
            Status.MarkFaulted(ex);
            _logger.LogCritical(ex, "Stopped receiving from {Source} after an unexpected error", Source);
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var receiver = _receiver!;
        int maxBatch = Math.Max(1, _transport.Capabilities.MaxReceiveBatchSize);
        var backoff = TimeSpan.Zero;
        int failures = 0;
        var token = _stopReceiving.Token;

        while (!token.IsCancellationRequested)
        {
            try
            {
                await _slots.WaitAsync(token).AnyContext();
            }
            catch (OperationCanceledException)
            {
                break;
            }

            int slots = 1;
            while (slots < maxBatch && _slots.CurrentCount > 0 && await _slots.WaitAsync(0, CancellationToken.None).AnyContext())
                slots++;

            IReadOnlyList<IncomingMessage> messages;
            try
            {
                messages = await receiver.ReceiveAsync(slots, ReceiveWait, token).AnyContext();
                backoff = TimeSpan.Zero;
                failures = 0;
                Status.RecordSuccess();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _slots.Release(slots);
                break;
            }
            catch (Exception ex)
            {
                _slots.Release(slots);
                Status.RecordFailure(ex);
                backoff = NextBackoff(backoff);
                bool replace = ex is MessageReceiverFaultedException || ++failures >= ReplaceReceiverAfterFailures;
                if (replace)
                    _logger.LogError(ex, "Receiver for {Source} failed; replacing it in {Backoff}", Source, backoff);
                else
                    _logger.LogError(ex, "Error receiving from {Source}; retrying in {Backoff}", Source, backoff);

                await _timeProvider.SafeDelay(backoff, token).AnyContext();
                if (replace)
                {
                    if (await ReplaceReceiverAsync(receiver, backoff, token).AnyContext() is not { } replacement)
                        break;

                    receiver = replacement;
                    failures = 0;
                }

                continue;
            }

            if (messages.Count < slots)
                _slots.Release(slots - messages.Count);
            if (messages.Count == 0)
                continue;

            var deliveries = new MessageDelivery[messages.Count];
            for (int i = 0; i < messages.Count; i++)
                deliveries[i] = new MessageDelivery(receiver, messages[i], _renewer);

            _renewer.Track(deliveries);
            foreach (var sequence in SplitIntoSequences(deliveries))
            {
                Interlocked.Add(ref _inFlight, sequence.Count);
                _ = Task.Run(() => ProcessSequenceAsync(sequence), CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Lets the failed receiver's in-flight messages finish (up to the replace timeout), disposes it and opens a new
    /// one, retrying with backoff. Returns null when the processor is stopping; <see cref="StopAsync"/> then closes
    /// whatever receiver is left.
    /// </summary>
    private async Task<IMessageReceiver?> ReplaceReceiverAsync(IMessageReceiver failed, TimeSpan backoff, CancellationToken token)
    {
        if (!await WaitForInFlightAsync(_settings.ReplaceTimeout, token).AnyContext() && token.IsCancellationRequested)
            return null;

        try
        {
            await failed.DisposeAsync().AnyContext();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error closing the failed receiver for {Source}", Source);
        }

        _receiver = null;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var receiver = await _transport.CreateReceiverAsync(Source, _receiverOptions, token).AnyContext();
                _receiver = receiver;
                _logger.LogInformation("Replaced the receiver for {Source}", Source);
                return receiver;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Status.RecordFailure(ex);
                backoff = NextBackoff(backoff);
                _logger.LogError(ex, "Could not open a new receiver for {Source}; retrying in {Backoff}", Source, backoff);
                await _timeProvider.SafeDelay(backoff, token).AnyContext();
            }
        }

        return null;
    }

    private static TimeSpan NextBackoff(TimeSpan backoff)
        => backoff == TimeSpan.Zero ? TimeSpan.FromSeconds(1) : TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxReceiveBackoff.Ticks));

    /// <summary>
    /// Splits a batch into sequences handled one message after another. Without ordered groups every message is its
    /// own sequence; with them, messages of the same group stay together in the order they were received.
    /// </summary>
    private List<List<MessageDelivery>> SplitIntoSequences(MessageDelivery[] deliveries)
    {
        var sequences = new List<List<MessageDelivery>>(deliveries.Length);
        Dictionary<string, List<MessageDelivery>>? groups = null;
        bool ordered = _transport.Capabilities.OrderedGroups;

        foreach (var delivery in deliveries)
        {
            if (!ordered || delivery.Message.GroupKey is not { } group)
            {
                sequences.Add([delivery]);
                continue;
            }

            groups ??= new Dictionary<string, List<MessageDelivery>>(StringComparer.Ordinal);
            if (groups.TryGetValue(group, out var sequence))
            {
                sequence.Add(delivery);
                continue;
            }

            sequence = [delivery];
            groups[group] = sequence;
            sequences.Add(sequence);
        }

        return sequences;
    }

    private async Task ProcessSequenceAsync(List<MessageDelivery> sequence)
    {
        bool release = false;
        foreach (var delivery in sequence)
        {
            try
            {
                if (delivery.IsLeaseLost)
                {
                    await delivery.StopRenewingAsync().AnyContext();
                    _logger.LogWarning("Lost the lease on message {MessageId} while it waited its turn; another consumer will handle it", delivery.Message.MessageId);
                    _handler.OnReleased(delivery, "lease-lost");
                    release = true;
                    continue;
                }

                // A later message of a group must not overtake an earlier one that will be delivered again.
                if (release || _abortHandlers.IsCancellationRequested)
                {
                    await delivery.SettleAsync(static (receiver, message, ct) => receiver.AbandonAsync(message, TimeSpan.Zero, ct), "abandon", _logger).AnyContext();
                    _handler.OnReleased(delivery, release ? "held" : "cancelled");
                    release = true;
                    continue;
                }

                release = !await _handler.HandleAsync(delivery, _abortHandlers.Token).AnyContext() && sequence.Count > 1;
            }
            catch (Exception ex)
            {
                release = true;
                _logger.LogError(ex, "Unexpected error processing message {MessageId} from {Source}", delivery.Message.MessageId, Source);
            }
            finally
            {
                await delivery.StopRenewingAsync().AnyContext();
                Interlocked.Decrement(ref _inFlight);
                _slots.Release();
            }
        }
    }

    private async Task<bool> WaitForInFlightAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Volatile.Read(ref _inFlight) > 0)
        {
            if (Stopwatch.GetTimestamp() >= deadline || cancellationToken.IsCancellationRequested)
                return false;

            try
            {
                await Task.Delay(25, cancellationToken).AnyContext();
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return true;
    }
}

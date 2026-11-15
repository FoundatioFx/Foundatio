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

    private readonly IMessageTransport _transport;
    private readonly MessageProcessorSettings _settings;
    private readonly IMessageDeliveryHandler _handler;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly ReceiverOptions _receiverOptions;
    private readonly LeaseRenewer _renewer;
    private readonly SemaphoreSlim _slots;
    private readonly RetryHolds _holds;
    private readonly ReceiveBackoff _backoff;
    private readonly int _maxBatch;
    private readonly int _concurrentReceives;
    private readonly CancellationTokenSource _stopReceiving = new();
    private readonly CancellationTokenSource _abortHandlers = new();
    private readonly object _lifecycleLock = new();
    private IMessageReceiver? _receiver;
    private Task _receiveLoop = Task.CompletedTask;
    private int _inFlight;
    private TaskCompletionSource _room = NewSignal();
    private int _waitingForRoom;
    private int _partialReceive;
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
        _holds = new RetryHolds(settings.MaxConcurrency, timeProvider, _stopReceiving.Token, ReleaseSlots);
        _backoff = new ReceiveBackoff(timeProvider);

        // Enough receives to fill every slot, as far as the transport allows: full batches and one for the rest.
        _maxBatch = Math.Max(1, capabilities.MaxReceiveBatchSize);
        int receivesToFill = (int)Math.Min((settings.MaxConcurrency + (long)_maxBatch - 1) / _maxBatch, Int32.MaxValue);
        _concurrentReceives = Math.Max(1, Math.Min(capabilities.MaxConcurrentReceives, receivesToFill));
    }

    public MessageSource Source { get; }

    /// <summary>Read by health checks. Receive errors are recorded here; they never stop the processor.</summary>
    public ProcessingStatusTracker Status { get; }

    /// <summary>Unsettled deliveries, including retries held in-process, with or without their handler slots.</summary>
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

        _logger.LogInformation("Receiving {Source} with {Handlers} (concurrency {MaxConcurrency}, receives in flight {ConcurrentReceives})",
            Source, _settings.Description, _settings.MaxConcurrency, _concurrentReceives);
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

        if (_receiver is { } receiver)
            await DisposeReceiverAsync(receiver).AnyContext();

        _renewer.Dispose();
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

    /// <summary>
    /// Runs the receives on one receiver at a time until the processor stops. When a receive decides the receiver must
    /// be replaced, the others on it stop, and once all have ended it is replaced and receiving starts on the new one.
    /// </summary>
    private async Task ReceiveLoopAsync()
    {
        var receiver = _receiver!;
        var token = _stopReceiving.Token;
        while (!token.IsCancellationRequested)
        {
            using (var receiving = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                if (_concurrentReceives == 1)
                {
                    await ReceiveFromAsync(receiver, receiving).AnyContext();
                }
                else
                {
                    var receives = new Task[_concurrentReceives];
                    for (int i = 0; i < receives.Length; i++)
                        receives[i] = Task.Run(() => ReceiveFromAsync(receiver, receiving), CancellationToken.None);

                    await Task.WhenAll(receives).AnyContext();
                }
            }

            if (token.IsCancellationRequested)
                break;

            await _timeProvider.SafeDelay(_backoff.Remaining, token).AnyContext();
            if (await ReplaceReceiverAsync(receiver, _backoff.Current, token).AnyContext() is not { } replacement)
                break;

            receiver = replacement;
            _backoff.Reset();
        }
    }

    /// <summary>
    /// One of the receives in flight on <paramref name="receiver"/>: waits for free slots, receives up to that many
    /// messages and starts handling them, until <paramref name="receiving"/> is cancelled, which it does itself when
    /// the receiver must be replaced. Every receive shares the slots, so together they never exceed the concurrency.
    /// Only one at a time asks for less than a full batch; the others wait until a full batch of slots is free, so slots
    /// that free up one by one under load do not each cost a broker receive.
    /// </summary>
    private async Task ReceiveFromAsync(IMessageReceiver receiver, CancellationTokenSource receiving)
    {
        var token = receiving.Token;
        try
        {
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
                while (slots < _maxBatch && _slots.CurrentCount > 0 && await _slots.WaitAsync(0, CancellationToken.None).AnyContext())
                    slots++;

                bool partial = slots < _maxBatch;
                if (partial && Interlocked.CompareExchange(ref _partialReceive, 1, 0) != 0)
                {
                    _slots.Release(slots);
                    await WaitForRoomAsync(token).AnyContext();
                    continue;
                }

                IReadOnlyList<IncomingMessage> messages;
                try
                {
                    long round = _backoff.BeginReceive(out var wait);
                    if (wait > TimeSpan.Zero)
                    {
                        _slots.Release(slots);
                        await _timeProvider.SafeDelay(wait, token).AnyContext();
                        continue;
                    }

                    try
                    {
                        messages = await receiver.ReceiveAsync(slots, ReceiveWait, token).AnyContext();
                        _backoff.RecordSuccess(round);
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
                        bool replace = _backoff.RecordFailure(ex, round, out bool counted, out var backoff);
                        if (replace)
                        {
                            _logger.LogError(ex, "Receiver for {Source} failed; replacing it in {Backoff}", Source, backoff);
                            receiving.Cancel();
                            break;
                        }

                        if (counted)
                            _logger.LogError(ex, "Error receiving from {Source}; retrying in {Backoff}", Source, backoff);
                        else
                            _logger.LogDebug(ex, "Another receive from {Source} failed at the same time; retrying in {Backoff}", Source, backoff);

                        continue;
                    }
                }
                finally
                {
                    if (partial)
                    {
                        Volatile.Write(ref _partialReceive, 0);
                        SignalRoom();
                    }
                }

                if (messages.Count < slots)
                    _slots.Release(slots - messages.Count);
                if (messages.Count == 0)
                    continue;

                var deliveries = new MessageDelivery[messages.Count];
                for (int i = 0; i < messages.Count; i++)
                    deliveries[i] = new MessageDelivery(receiver, messages[i], _renewer, _holds);

                _renewer.Track(deliveries);
                foreach (var sequence in SplitIntoSequences(deliveries))
                {
                    Interlocked.Add(ref _inFlight, sequence.Count);
                    _ = Task.Run(() => ProcessSequenceAsync(sequence), CancellationToken.None);
                }
            }
        }
        catch
        {
            // Stop the other receives, so the unexpected error ends the receive loop instead of waiting for them forever.
            receiving.Cancel();
            throw;
        }
    }

    /// <summary>Waits until no receive asks for a partial batch, or a full batch of slots is free.</summary>
    private async Task WaitForRoomAsync(CancellationToken token)
    {
        Interlocked.Increment(ref _waitingForRoom);
        try
        {
            var room = Volatile.Read(ref _room).Task;
            if (Volatile.Read(ref _partialReceive) == 0 || _slots.CurrentCount >= _maxBatch)
                return;

            await room.WaitAsync(token).AnyContext();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Interlocked.Decrement(ref _waitingForRoom);
        }
    }

    /// <summary>Wakes the receives waiting for room, if any.</summary>
    private void SignalRoom()
    {
        if (Volatile.Read(ref _waitingForRoom) > 0)
            Interlocked.Exchange(ref _room, NewSignal()).TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void ReleaseSlots(int count)
    {
        _slots.Release(count);
        if (_slots.CurrentCount >= _maxBatch)
            SignalRoom();
    }

    /// <summary>
    /// Lets the failed receiver's in-flight messages finish (up to the replace timeout), disposes it and opens a new
    /// one, retrying with backoff. Returns null when the processor is stopping; <see cref="StopAsync"/> then closes
    /// whatever receiver is left.
    /// </summary>
    private async Task<IMessageReceiver?> ReplaceReceiverAsync(IMessageReceiver failed, TimeSpan backoff, CancellationToken token)
    {
        if (!await WaitForInFlightAsync(_settings.ReplaceTimeout, token).AnyContext())
        {
            if (token.IsCancellationRequested)
                return null;

            // Disposing the receiver releases these messages to its replacement, so stop their handlers first.
            _logger.LogWarning("Cancelling {Count} in-flight messages from the failed receiver for {Source}; they will be delivered again", Volatile.Read(ref _inFlight), Source);
            _renewer.MarkLost(failed);
            if (!await WaitForInFlightAsync(AbortGracePeriod, token).AnyContext() && token.IsCancellationRequested)
                return null;
        }

        await DisposeReceiverAsync(failed).AnyContext();
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
                backoff = ReceiveBackoff.Next(backoff);
                _logger.LogError(ex, "Could not open a new receiver for {Source}; retrying in {Backoff}", Source, backoff);
                await _timeProvider.SafeDelay(backoff, token).AnyContext();
            }
        }

        return null;
    }

    /// <summary>
    /// Marks the deliveries <paramref name="receiver"/> still holds lost, so handlers that outlived the drain are
    /// cancelled and never settle on it, then disposes it, which releases their messages to another consumer.
    /// </summary>
    private async Task DisposeReceiverAsync(IMessageReceiver receiver)
    {
        _renewer.MarkLost(receiver);
        try
        {
            await receiver.DisposeAsync().AnyContext();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error closing a receiver for {Source}", Source);
        }
    }

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

    /// <summary>
    /// Handles a sequence's deliveries one after another. Each holds a handler slot until it is settled, unless a retry
    /// hold gave the slots of the rest of the sequence to other messages (see <see cref="RetryHolds"/>).
    /// </summary>
    private async Task ProcessSequenceAsync(List<MessageDelivery> sequence)
    {
        bool release = false, holdsSlots = true;
        for (int i = 0; i < sequence.Count; i++)
        {
            var delivery = sequence[i];
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

                delivery.SequenceSlots = sequence.Count - i;
                release = !await _handler.HandleAsync(delivery, _abortHandlers.Token).AnyContext() && sequence.Count > 1;
            }
            catch (Exception ex)
            {
                release = true;
                _logger.LogError(ex, "Unexpected error processing message {MessageId} from {Source}", delivery.Message.MessageId, Source);
            }
            finally
            {
                holdsSlots &= !delivery.SlotsReleased;
                await delivery.StopRenewingAsync().AnyContext();
                Interlocked.Decrement(ref _inFlight);
                if (holdsSlots)
                    ReleaseSlots(1);
                else
                    _holds.Leave();
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

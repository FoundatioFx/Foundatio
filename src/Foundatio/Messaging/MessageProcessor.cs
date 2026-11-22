using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Foundatio.Messaging.Transport;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging;

/// <summary>
/// Receives from one source with bounded concurrency, keeps every delivery's lease renewed until it is settled,
/// replaces a failed receiver and drains on shutdown. What happens to each delivery is up to its <see cref="IMessageDeliveryHandler"/>.
/// A delivery's handler slot goes to the next message while it settles, when there is room (see <see cref="SettlementSlots"/>).
/// When the transport declares <see cref="TransportCapabilities.ReceiveAhead"/>, receives may also run ahead of the free
/// slots, into a buffer that gives each message a slot as one frees (see <see cref="ReceiveAhead"/>).
/// </summary>
internal sealed class MessageProcessor : IAsyncDisposable
{
    // Long enough to be one SQS long poll; stopping cancels a receive in progress, so shutdown does not wait for it.
    private static readonly TimeSpan ReceiveWait = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan AbortGracePeriod = TimeSpan.FromSeconds(5);

    // What happens to buffered messages: they are given slots, dropped unstarted because their receiver is being
    // replaced, or released because shutdown stopped waiting.
    private const int BufferPumping = 0;
    private const int BufferDropping = 1;
    private const int BufferReleasing = 2;

    private readonly IMessageTransport _transport;
    private readonly MessageProcessorSettings _settings;
    private readonly IMessageDeliveryHandler _handler;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly ReceiverOptions _receiverOptions;
    private readonly LeaseRenewer _renewer;
    private readonly SemaphoreSlim _slots;
    private readonly RetryHolds _holds;
    private readonly SettlementSlots _settlements;
    private readonly ReceiveBackoff _backoff;
    private readonly Channel<MessageDelivery>? _inOrder;
    private readonly int _maxBatch;
    private readonly int _fullBatch;
    private readonly int _concurrentReceives;
    private readonly CancellationTokenSource _stopReceiving = new();
    private readonly CancellationTokenSource _abortHandlers = new();
    private readonly object _lifecycleLock = new();
    private IMessageReceiver? _receiver;
    private StrongBox<int> _receiverInFlight = new();
    private readonly List<Task> _drains = [];
    private Task _receiveLoop = Task.CompletedTask;
    private int _inFlight;
    private TaskCompletionSource _room = NewSignal();
    private int _waitingForRoom;
    private int _partialReceive;
    private bool _started;
    private bool _stopped;

    // Reading ahead. Receives at depth zero take slots as above; otherwise they ask for up to the free slots plus the
    // depth, less what is buffered or already asked for, and Pump gives each buffered message a slot.
    private readonly ReceiveAhead? _ahead;
    private readonly int _aheadBatch;
    private readonly int _aheadReceives;
    private readonly object _bufferLock = new();
    private readonly Queue<List<MessageDelivery>> _buffer = new();
    private int _buffered;
    private long _bufferedBytes;
    private int _requested;
    private int _pumpSlots;
    private int _bufferMode;
    private int _pollingReceives = 1;
    private TaskCompletionSource _turn = NewSignal();

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
        _settlements = new SettlementSlots(settings.MaxConcurrency, Math.Max(1, capabilities.MaxReceiveBatchSize), ReleaseSlots);
        _backoff = new ReceiveBackoff(timeProvider);

        // Enough receives to fill every slot, as far as the transport allows: full batches and one for the rest.
        _maxBatch = Math.Max(1, capabilities.MaxReceiveBatchSize);
        int receivesToFill = (int)Math.Min((settings.MaxConcurrency + (long)_maxBatch - 1) / _maxBatch, Int32.MaxValue);
        _concurrentReceives = Math.Max(1, Math.Min(capabilities.MaxConcurrentReceives, receivesToFill));
        _fullBatch = _maxBatch;
        if (settings.InOrder)
        {
            _concurrentReceives = 1;
            _fullBatch = Math.Min(_maxBatch, settings.MaxConcurrency);
            _inOrder = Channel.CreateUnbounded<MessageDelivery>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        }
        else if (capabilities.ReceiveAhead)
        {
            // With ordered groups, batches no larger than the slots, so a batch's group always fits in them; without
            // them, or with one message of a group out at a time, every message is its own sequence, so full batches.
            // And one receive more than it takes to fill the slots, to read ahead.
            _aheadBatch = capabilities.OrderedGroups && !capabilities.OneOutstandingPerGroup ? Math.Min(_maxBatch, settings.MaxConcurrency) : _maxBatch;
            _aheadReceives = (int)Math.Max(1, Math.Min(capabilities.MaxConcurrentReceives, receivesToFill + 1L));
            _ahead = new ReceiveAhead(settings.MaxConcurrency, _aheadBatch, _aheadReceives, lease, timeProvider.TimestampFrequency);
        }
    }

    public MessageSource Source { get; }

    /// <summary>Read by health checks. Receive errors are recorded here; they never stop the processor.</summary>
    public ProcessingStatusTracker Status { get; }

    /// <summary>Unsettled deliveries, including retries held in-process and buffered ones, with or without their handler slots.</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>Received ahead of the free handler slots, and waiting for one.</summary>
    public int Buffered => Math.Max(0, Volatile.Read(ref _buffered));

    public ProcessingStatus GetStatus()
    {
        int buffered = Buffered;
        return Status.GetStatus(Source.ToString(), Math.Max(0, InFlight - buffered), buffered: buffered);
    }

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

                // Handlers must not run in the context (activity, message context) of whoever started the processor,
                // such as the request that called SubscribeAsync.
                bool suppress = !ExecutionContext.IsFlowSuppressed();
                var flow = suppress ? ExecutionContext.SuppressFlow() : default;
                try
                {
                    // Ends once the receive loop has ended and every delivery it received was handed to the handler.
                    if (_inOrder is not null)
                        _ = Task.Run(RunInOrderLoopAsync, CancellationToken.None);

                    _receiveLoop = Task.Run(RunReceiveLoopAsync, CancellationToken.None);
                }
                finally
                {
                    if (suppress)
                        flow.Undo();
                }
            }
        }

        if (!started)
        {
            await receiver.DisposeAsync().AnyContext();
            return;
        }

        if (_ahead is null)
        {
            _logger.LogInformation("Receiving {Source} with {Handlers} (concurrency {MaxConcurrency}, receives in flight {ConcurrentReceives})",
                Source, _settings.Description, _settings.MaxConcurrency, _concurrentReceives);
        }
        else
        {
            _logger.LogInformation("Receiving {Source} with {Handlers} (concurrency {MaxConcurrency}, receives in flight {ConcurrentReceives}, reading ahead)",
                Source, _settings.Description, _settings.MaxConcurrency, _aheadReceives);
        }
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
            if (_ahead is not null)
                CloseBuffer(BufferReleasing);

            _abortHandlers.Cancel();
            await WaitForInFlightAsync(AbortGracePeriod, CancellationToken.None).AnyContext();
        }

        if (_receiver is { } receiver)
            await DisposeReceiverAsync(receiver).AnyContext();

        // Receivers that failed are disposed once their own deliveries have finished, which the drain above waited for.
        await Task.WhenAll(_drains).AnyContext();
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
        finally
        {
            _inOrder?.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Runs the receives on one receiver at a time until the processor stops. When a receive decides the receiver must
    /// be replaced, the others on it stop, and once all have ended a new one is opened and receiving starts on it at
    /// once, while the failed one is drained in the background (see <see cref="DrainReceiverAsync"/>).
    /// </summary>
    private async Task ReceiveLoopAsync()
    {
        var receiver = _receiver!;
        var token = _stopReceiving.Token;
        while (!token.IsCancellationRequested)
        {
            using (var receiving = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                if (_ahead is not null)
                {
                    await ReceiveAheadOnAsync(receiver, receiving).AnyContext();
                }
                else if (_concurrentReceives == 1)
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
            if (await OpenReplacementAsync(_backoff.Current, token).AnyContext() is not { } replacement)
                break;

            var failedInFlight = _receiverInFlight;
            _drains.RemoveAll(static drain => drain.IsCompleted);
            var failed = receiver;
            _drains.Add(Task.Run(() => DrainReceiverAsync(failed, failedInFlight), CancellationToken.None));
            _receiverInFlight = new StrongBox<int>();
            _receiver = receiver = replacement;
            _backoff.Reset();
            if (_ahead is not null)
                ReopenBuffer();
        }
    }

    /// <summary>
    /// One of the receives in flight on <paramref name="receiver"/>: waits for free slots, receives up to that many
    /// messages and starts handling them, until <paramref name="receiving"/> is cancelled, which it does itself when
    /// the receiver must be replaced. Every receive shares the slots, so together they never exceed the concurrency.
    /// Only one at a time asks for less than a full batch; the others wait until a full batch of slots is free, so slots
    /// that free up one by one under load do not each cost a broker receive. In order, the single receive waits for a
    /// full batch of free slots while any delivery is in flight.
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

                bool partial = slots < _fullBatch;
                if (partial && (_inOrder is not null ? Volatile.Read(ref _inFlight) > 0 : Interlocked.CompareExchange(ref _partialReceive, 1, 0) != 0))
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
                        if (RecordReceiveFailure(ex, round))
                        {
                            receiving.Cancel();
                            break;
                        }

                        continue;
                    }
                }
                finally
                {
                    if (partial && _inOrder is null)
                    {
                        Volatile.Write(ref _partialReceive, 0);
                        SignalRoom();
                    }
                }

                if (messages.Count < slots)
                    _slots.Release(slots - messages.Count);
                if (messages.Count == 0)
                    continue;

                var receiverInFlight = _receiverInFlight;
                Interlocked.Add(ref receiverInFlight.Value, messages.Count);
                var deliveries = new MessageDelivery[messages.Count];
                for (int i = 0; i < messages.Count; i++)
                    deliveries[i] = new MessageDelivery(receiver, messages[i], _renewer, _holds, _settlements) { ReceiverInFlight = receiverInFlight };

                _renewer.Track(deliveries);
                if (_inOrder is not null)
                {
                    Interlocked.Add(ref _inFlight, deliveries.Length);
                    foreach (var delivery in deliveries)
                        _inOrder.Writer.TryWrite(delivery);

                    continue;
                }

                foreach (var sequence in SplitIntoSequences(deliveries))
                {
                    Interlocked.Add(ref _inFlight, sequence.Count);
                    StartSequence(sequence);
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

    /// <summary>Runs the receives on <paramref name="receiver"/> when reading ahead, one more than it takes to fill the slots.</summary>
    private async Task ReceiveAheadOnAsync(IMessageReceiver receiver, CancellationTokenSource receiving)
    {
        if (_aheadReceives == 1)
        {
            await ReceiveAheadFromAsync(receiver, receiving, 0).AnyContext();
            return;
        }

        var receives = new Task[_aheadReceives];
        for (int i = 0; i < receives.Length; i++)
        {
            int index = i;
            receives[i] = Task.Run(() => ReceiveAheadFromAsync(receiver, receiving, index), CancellationToken.None);
        }

        await Task.WhenAll(receives).AnyContext();
    }

    /// <summary>
    /// One of the receives in flight on <paramref name="receiver"/> when reading ahead. At depth zero it receives into
    /// free slots, as <see cref="ReceiveFromAsync"/> does, once the buffer has drained. Otherwise it takes no slots: it
    /// asks for up to a batch of the free slots plus the depth, less what is buffered or already asked for, and buffers
    /// what it gets for <see cref="Pump"/>. Only <see cref="_pollingReceives"/> of the receives poll: one that returns fewer
    /// messages than it asked for parks the others once their calls return, and each that returns as many lets one more poll.
    /// </summary>
    private async Task ReceiveAheadFromAsync(IMessageReceiver receiver, CancellationTokenSource receiving, int index)
    {
        var token = receiving.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!MayPoll(index))
                {
                    await WaitForTurnAsync(index, token).AnyContext();
                    continue;
                }

                int count;
                bool partial, holdsSlots = GetDepth() == 0;
                if (holdsSlots)
                {
                    if (Volatile.Read(ref _buffered) > 0)
                    {
                        await WaitForRoomAsync(token).AnyContext();
                        continue;
                    }

                    try
                    {
                        await _slots.WaitAsync(token).AnyContext();
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    count = 1;
                    while (count < _maxBatch && _slots.CurrentCount > 0 && await _slots.WaitAsync(0, CancellationToken.None).AnyContext())
                        count++;

                    // The depth may have risen, or another receive buffered messages, while this one waited for a slot.
                    if (GetDepth() > 0 || Volatile.Read(ref _buffered) > 0)
                    {
                        ReleaseSlots(count);
                        continue;
                    }

                    partial = count < _fullBatch;
                    if (partial && Interlocked.CompareExchange(ref _partialReceive, 1, 0) != 0)
                    {
                        ReleaseSlots(count);
                        await WaitForRoomAsync(token).AnyContext();
                        continue;
                    }
                }
                else if (!TryReserve(out count, out partial))
                {
                    await WaitForRoomAsync(token).AnyContext();
                    continue;
                }

                long round = _backoff.BeginReceive(out var wait);
                if (wait > TimeSpan.Zero)
                {
                    EndReceive(count, holdsSlots, partial);
                    await _timeProvider.SafeDelay(wait, token).AnyContext();
                    continue;
                }

                IReadOnlyList<IncomingMessage> messages;
                long started = _timeProvider.GetTimestamp();
                try
                {
                    messages = await receiver.ReceiveAsync(count, ReceiveWait, token).AnyContext();
                    _backoff.RecordSuccess(round);
                    Status.RecordSuccess();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    EndReceive(count, holdsSlots, partial);
                    break;
                }
                catch (Exception ex)
                {
                    EndReceive(count, holdsSlots, partial);
                    if (RecordReceiveFailure(ex, round))
                    {
                        // The receiver is disposed once its handlers finish, which releases the messages it buffered.
                        CloseBuffer(BufferDropping);
                        receiving.Cancel();
                        break;
                    }

                    continue;
                }

                if (messages.Count == count && _ahead!.RecordReceiveTime(_timeProvider.GetTimestamp() - started))
                    OnDepthRaised();

                UpdatePolling(count, messages.Count);
                if (messages.Count == 0)
                {
                    EndReceive(count, holdsSlots, partial);
                    continue;
                }

                var deliveries = CreateDeliveries(receiver, messages, started, out long bytes);
                _renewer.Track(deliveries);
                Interlocked.Add(ref _inFlight, deliveries.Length);
                if (!holdsSlots)
                {
                    Buffer(deliveries, bytes, count, partial);
                    continue;
                }

                EndReceive(count - deliveries.Length, holdsSlots, partial);
                foreach (var sequence in SplitIntoSequences(deliveries))
                    StartSequence(sequence);
            }
        }
        catch
        {
            receiving.Cancel();
            throw;
        }
    }

    /// <summary>
    /// Reserves a receive that reads ahead: up to a batch of the free slots plus the depth, less what is buffered or
    /// already asked for. It starts with room for a full batch, or as the one partial receive while nothing is buffered.
    /// </summary>
    private bool TryReserve(out int count, out bool partial)
    {
        lock (_bufferLock)
        {
            count = Math.Min(_aheadBatch, _slots.CurrentCount + GetDepth() - _buffered - _requested);
            partial = count < _aheadBatch;
            if (count <= 0 || partial && (_buffered > 0 || Interlocked.CompareExchange(ref _partialReceive, 1, 0) != 0))
                return false;

            _requested += count;
            return true;
        }
    }

    /// <summary>
    /// Ends a receive that gets no further than this: gives back <paramref name="unused"/> of the slots it holds, or of
    /// the messages it reserved, and lets another receive ask for a partial batch.
    /// </summary>
    private void EndReceive(int unused, bool holdsSlots, bool partial)
    {
        if (!holdsSlots)
        {
            lock (_bufferLock)
                _requested -= unused;
        }

        if (partial)
            Volatile.Write(ref _partialReceive, 0);

        if (holdsSlots && unused > 0)
            ReleaseSlots(unused);
        else
            SignalRoomAhead();
    }

    /// <summary>Queues a receive's deliveries, as the sequences they are handled in, for <see cref="Pump"/>.</summary>
    private void Buffer(MessageDelivery[] deliveries, long bytes, int requested, bool partial)
    {
        var sequences = SplitIntoSequences(deliveries);
        int count = deliveries.Length;
        if (_aheadBatch > _settings.MaxConcurrency)
            count -= ReleaseBeyondSlots(sequences, ref bytes);

        lock (_bufferLock)
        {
            foreach (var sequence in sequences)
                _buffer.Enqueue(sequence);

            _buffered += count;
            _bufferedBytes += bytes;
            _requested -= requested;
            if (partial)
                Volatile.Write(ref _partialReceive, 0);
        }

        Pump();
        SignalRoomAhead();
    }

    /// <summary>
    /// Gives buffered sequences the handler slots they need, one per message, in the order they were received, and
    /// starts each once it has them all. Once the buffer is closed, releases them instead without running their handlers.
    /// </summary>
    private void Pump()
    {
        if (Volatile.Read(ref _buffered) == 0)
            return;

        List<List<MessageDelivery>>? starting = null, releasing = null;
        int mode, returned = 0;
        bool overCap = false, emptied;
        lock (_bufferLock)
        {
            mode = _bufferMode;
            if (mode != BufferPumping)
            {
                releasing = [.. _buffer];
                _buffer.Clear();
                returned = _pumpSlots;
                _pumpSlots = 0;
                _buffered = 0;
                _bufferedBytes = 0;
            }
            else
            {
                overCap = _bufferedBytes > ReceiveAhead.MaxBufferedBytes;
                while (_buffer.TryPeek(out var sequence))
                {
                    while (_pumpSlots < sequence.Count && _slots.Wait(0))
                    {
                        _pumpSlots++;
                        _buffered--;
                    }

                    if (_pumpSlots < sequence.Count)
                        break;

                    _buffer.Dequeue();
                    _pumpSlots = 0;
                    foreach (var delivery in sequence)
                        _bufferedBytes -= delivery.Message.Body.Length;

                    (starting ??= []).Add(sequence);
                }
            }

            emptied = _buffer.Count == 0;
        }

        if (starting is not null)
        {
            foreach (var sequence in starting)
                StartSequence(sequence);
        }

        if (releasing is not null)
        {
            if (mode == BufferDropping)
                _logger.LogWarning("Releasing {Count} buffered messages from the failed receiver for {Source}; they will be delivered again", releasing.Sum(r => r.Count), Source);

            foreach (var sequence in releasing)
                _ = ReleaseUnstartedAsync(sequence, mode);
        }

        if (returned > 0)
            _slots.Release(returned);

        // While the bodies exceeded the cap, the depth was zero, which parks the receives beyond those that fill the slots.
        if (overCap && starting is not null && GetDepth() > 0)
            SignalTurn();

        if (emptied)
            SignalRoom();
    }

    /// <summary>
    /// A sequence must fit in the handler slots to start. With batches larger than the slots, which a transport that
    /// hands out one message of a group at a time allows, a longer sequence means it broke that promise: its messages
    /// beyond the slots are abandoned, without running their handlers, to be delivered again in order. Returns how many.
    /// </summary>
    private int ReleaseBeyondSlots(List<List<MessageDelivery>> sequences, ref long bytes)
    {
        int slots = _settings.MaxConcurrency, released = 0;
        foreach (var sequence in sequences)
        {
            if (sequence.Count <= slots)
                continue;

            var beyond = sequence.GetRange(slots, sequence.Count - slots);
            sequence.RemoveRange(slots, beyond.Count);
            foreach (var delivery in beyond)
                bytes -= delivery.Message.Body.Length;

            _logger.LogError("A receive from {Source} returned {Count} messages of group {GroupKey}, though the transport declares OneOutstandingPerGroup; " +
                "releasing those beyond the {Slots} handler slots to be delivered again", Source, sequence.Count + beyond.Count, sequence[0].Message.GroupKey, slots);
            released += beyond.Count;
            _ = ReleaseUnstartedAsync(beyond, BufferReleasing, "held");
        }

        return released;
    }

    /// <summary>
    /// Releases a buffered sequence whose handlers never ran. At shutdown it is abandoned, which delivers it again; when
    /// its receiver is being replaced it is marked lost instead, and disposing the receiver releases it.
    /// </summary>
    private async Task ReleaseUnstartedAsync(List<MessageDelivery> sequence, int mode, string outcome = "cancelled")
    {
        foreach (var delivery in sequence)
        {
            try
            {
                if (mode == BufferDropping || delivery.IsLeaseLost)
                {
                    await delivery.StopRenewingAsync().AnyContext();
                    delivery.MarkLeaseLost();
                    _handler.OnReleased(delivery, "lease-lost");
                }
                else
                {
                    await delivery.SettleAsync(static (receiver, message, ct) => receiver.AbandonAsync(message, TimeSpan.Zero, ct), "abandon", _logger).AnyContext();
                    _handler.OnReleased(delivery, outcome);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error releasing message {MessageId} from {Source}", delivery.Message.MessageId, Source);
            }
            finally
            {
                await delivery.StopRenewingAsync().AnyContext();
                Leave(delivery);
            }
        }
    }

    /// <summary>Stops giving buffered messages slots, and releases them (see <see cref="ReleaseUnstartedAsync"/>).</summary>
    private void CloseBuffer(int mode)
    {
        lock (_bufferLock)
        {
            if (_bufferMode < mode)
                _bufferMode = mode;
        }

        Pump();
    }

    /// <summary>Starts giving buffered messages slots again, on a new receiver, which starts with one poll.</summary>
    private void ReopenBuffer()
    {
        lock (_bufferLock)
            _bufferMode = BufferPumping;

        Volatile.Write(ref _pollingReceives, 1);
    }

    /// <summary>The depth to read ahead to now, within the buffered-bytes cap (see <see cref="ReceiveAhead.GetDepth"/>).</summary>
    private int GetDepth() => _ahead!.GetDepth(Volatile.Read(ref _buffered), Interlocked.Read(ref _bufferedBytes));

    /// <summary>
    /// Whether a receive that reads ahead could start. At depth zero, once the buffer has drained, as for receives
    /// into free slots; otherwise with room for a full batch, or for a partial one while nothing is buffered.
    /// </summary>
    private bool HasRoomAhead()
    {
        int buffered = Volatile.Read(ref _buffered), depth = GetDepth();
        if (depth == 0)
            return buffered == 0 && (Volatile.Read(ref _partialReceive) == 0 || _slots.CurrentCount >= _fullBatch);

        int room = _slots.CurrentCount + depth - buffered - Volatile.Read(ref _requested);
        return room >= _aheadBatch || room > 0 && buffered == 0 && Volatile.Read(ref _partialReceive) == 0;
    }

    private void SignalRoomAhead()
    {
        if (Volatile.Read(ref _waitingForRoom) > 0 && HasRoomAhead())
            SignalRoom();
    }

    /// <summary>The depth rose, which may let receives waiting for room or for their turn poll.</summary>
    private void OnDepthRaised()
    {
        SignalRoom();
        SignalTurn();
    }

    /// <summary>
    /// Whether the receive at <paramref name="index"/> may poll: within <see cref="_pollingReceives"/>, and at depth zero
    /// within the receives it takes to fill the slots.
    /// </summary>
    private bool MayPoll(int index) => index < Math.Min(Volatile.Read(ref _pollingReceives), GetDepth() > 0 ? _aheadReceives : _concurrentReceives);

    private async Task WaitForTurnAsync(int index, CancellationToken token)
    {
        var turn = Volatile.Read(ref _turn).Task;
        if (MayPoll(index))
            return;

        try
        {
            await turn.WaitAsync(token).AnyContext();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void SignalTurn() => Interlocked.Exchange(ref _turn, NewSignal()).TrySetResult();

    /// <summary>
    /// A receive that returned fewer messages than it asked for found the source drained, so only the first receive keeps
    /// polling; one that returned as many found a backlog, so one more may poll.
    /// </summary>
    private void UpdatePolling(int asked, int received)
    {
        if (_aheadReceives == 1)
            return;

        if (received < asked)
        {
            Volatile.Write(ref _pollingReceives, 1);
            return;
        }

        int polling = Volatile.Read(ref _pollingReceives);
        if (polling < _aheadReceives && Interlocked.CompareExchange(ref _pollingReceives, polling + 1, polling) == polling)
            SignalTurn();
    }

    /// <summary>Records how long <paramref name="delivery"/> kept its slot: until its handler returned, or until now.</summary>
    private void RecordSlotTime(MessageDelivery delivery, long started)
    {
        long returned = delivery.HandlerReturnedAt;
        if (_ahead!.RecordSlotTime((returned != 0 ? returned : _timeProvider.GetTimestamp()) - started))
            OnDepthRaised();
    }

    /// <summary>
    /// Wraps received messages for reading ahead, totals their <paramref name="bytes"/> of bodies, and records those and
    /// the leases a broker that decides them granted. A lease is counted from when the receive began, at timestamp
    /// <paramref name="started"/>: a broker may count it from the request, so counting from now would lose however long a
    /// long poll waited.
    /// </summary>
    private MessageDelivery[] CreateDeliveries(IMessageReceiver receiver, IReadOnlyList<IncomingMessage> messages, long started, out long bytes)
    {
        var deliveries = new MessageDelivery[messages.Count];
        var receiverInFlight = _receiverInFlight;
        Interlocked.Add(ref receiverInFlight.Value, messages.Count);
        DateTimeOffset? receiveStartedAt = null;
        bytes = 0;
        for (int i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            deliveries[i] = new MessageDelivery(receiver, message, _renewer, _holds, _settlements, _timeProvider) { ReceiverInFlight = receiverInFlight };
            bytes += message.Body.Length;
            if (message.LeaseExpiresAt is { } expiresAt)
            {
                receiveStartedAt ??= _timeProvider.GetUtcNow() - _timeProvider.GetElapsedTime(started);
                if (_ahead!.RecordLease(expiresAt - receiveStartedAt.Value))
                    OnDepthRaised();
            }
        }

        if (_ahead!.RecordBodies(deliveries.Length, bytes))
            OnDepthRaised();

        return deliveries;
    }

    /// <summary>Records a failed receive and logs it. Returns true when the receiver must be replaced.</summary>
    private bool RecordReceiveFailure(Exception ex, long round)
    {
        Status.RecordFailure(ex);
        bool replace = _backoff.RecordFailure(ex, round, out bool counted, out var backoff);
        if (replace)
        {
            _logger.LogError(ex, "Receiver for {Source} failed; replacing it in {Backoff}", Source, backoff);
            return true;
        }

        if (counted)
            _logger.LogError(ex, "Error receiving from {Source}; retrying in {Backoff}", Source, backoff);
        else
            _logger.LogDebug(ex, "Another receive from {Source} failed at the same time; retrying in {Backoff}", Source, backoff);

        return false;
    }

    /// <summary>
    /// Waits until no receive asks for a partial batch (or, in order, none is in flight), or a full batch of slots is
    /// free; when reading ahead, until <see cref="HasRoomAhead"/>.
    /// </summary>
    private async Task WaitForRoomAsync(CancellationToken token)
    {
        Interlocked.Increment(ref _waitingForRoom);
        try
        {
            var room = Volatile.Read(ref _room).Task;
            if (HasRoom())
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

    private bool HasRoom()
    {
        if (_ahead is not null)
            return HasRoomAhead();

        return (_inOrder is not null ? Volatile.Read(ref _inFlight) == 0 : Volatile.Read(ref _partialReceive) == 0) || _slots.CurrentCount >= _fullBatch;
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
        if (_ahead is not null)
        {
            Pump();
            SignalRoomAhead();
        }
        else if (_slots.CurrentCount >= _fullBatch)
        {
            SignalRoom();
        }
    }

    /// <summary>
    /// Opens a receiver to replace a failed one, retrying with backoff. Returns null when the processor is stopping;
    /// <see cref="StopAsync"/> then closes the failed receiver, which is still <see cref="_receiver"/>.
    /// </summary>
    private async Task<IMessageReceiver?> OpenReplacementAsync(TimeSpan backoff, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var receiver = await _transport.CreateReceiverAsync(Source, _receiverOptions, token).AnyContext();
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
    /// Lets the deliveries a failed receiver still holds finish, up to the replace timeout, while its replacement
    /// already receives, then disposes it. Their messages stay leased to it until then, so on transports whose leases
    /// expire their redeliveries never run alongside them. Handlers still running at the timeout are cancelled first,
    /// since disposing the receiver releases their messages to another consumer. At shutdown, <see cref="StopAsync"/>
    /// drains every delivery, and this waits only until its abort.
    /// </summary>
    private async Task DrainReceiverAsync(IMessageReceiver failed, StrongBox<int> inFlight)
    {
        try
        {
            if (!await WaitForNoneAsync(() => Volatile.Read(ref inFlight.Value), _settings.ReplaceTimeout, _abortHandlers.Token).AnyContext())
            {
                if (!_abortHandlers.IsCancellationRequested)
                {
                    _logger.LogWarning("Cancelling {Count} in-flight messages from the failed receiver for {Source}; they will be delivered again", Volatile.Read(ref inFlight.Value), Source);
                    _renewer.MarkLost(failed);
                }

                await WaitForNoneAsync(() => Volatile.Read(ref inFlight.Value), AbortGracePeriod, CancellationToken.None).AnyContext();
            }
        }
        finally
        {
            await DisposeReceiverAsync(failed).AnyContext();
        }
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
    /// Handles a sequence on the thread pool, as <see cref="Task.Run(Func{Task})"/> would, without the task, delegate and
    /// unwrapping that nobody awaits.
    /// </summary>
    private void StartSequence(List<MessageDelivery> sequence)
    {
        ThreadPool.QueueUserWorkItem(static state => _ = state.Processor.ProcessSequenceAsync(state.Sequence), (Processor: this, Sequence: sequence), preferLocal: false);
    }

    /// <summary>
    /// Handles a sequence's deliveries one after another, each once the one before it has settled. Each holds a handler
    /// slot until it is settled, unless its settlement gave its slot to other messages (see <see cref="SettlementSlots"/>)
    /// or a retry hold gave the slots of the rest of the sequence away (see <see cref="RetryHolds"/>).
    /// </summary>
    private async Task ProcessSequenceAsync(List<MessageDelivery> sequence)
    {
        bool release = false, holdsSlots = true;
        for (int i = 0; i < sequence.Count; i++)
        {
            var delivery = sequence[i];
            delivery.SequenceSlots = holdsSlots ? sequence.Count - i : 0;
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

                long started = _ahead is null ? 0 : _timeProvider.GetTimestamp();
                release = !await _handler.HandleAsync(delivery, _abortHandlers.Token).AnyContext() && sequence.Count > 1;
                if (_ahead is not null)
                    RecordSlotTime(delivery, started);
            }
            catch (Exception ex)
            {
                release = true;
                _logger.LogError(ex, "Unexpected error processing message {MessageId} from {Source}", delivery.Message.MessageId, Source);
                await AbandonUnsettledAsync(delivery).AnyContext();
            }
            finally
            {
                holdsSlots &= !delivery.SlotsReleased;
                await FinishAsync(delivery, holdsSlots).AnyContext();
            }
        }
    }

    /// <summary>
    /// Handles the in-order deliveries one at a time as they were received. Each is handed to its handler once the one
    /// before it has returned, while that one may still be settling.
    /// </summary>
    private async Task RunInOrderLoopAsync()
    {
        var reader = _inOrder!.Reader;
        while (await reader.WaitToReadAsync(CancellationToken.None).AnyContext())
        {
            while (reader.TryRead(out var delivery))
            {
                var handlerReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                delivery.HandlerReturned = handlerReturned;
                _ = ProcessInOrderAsync(delivery);
                await handlerReturned.Task.AnyContext();
            }
        }
    }

    private async Task ProcessInOrderAsync(MessageDelivery delivery)
    {
        delivery.SequenceSlots = 1;
        try
        {
            if (delivery.IsLeaseLost)
            {
                await delivery.StopRenewingAsync().AnyContext();
                _logger.LogWarning("Lost the lease on message {MessageId} while it waited its turn; another consumer will handle it", delivery.Message.MessageId);
                _handler.OnReleased(delivery, "lease-lost");
            }
            else if (_abortHandlers.IsCancellationRequested)
            {
                await delivery.SettleAsync(static (receiver, message, ct) => receiver.AbandonAsync(message, TimeSpan.Zero, ct), "abandon", _logger).AnyContext();
                _handler.OnReleased(delivery, "cancelled");
            }
            else
            {
                await _handler.HandleAsync(delivery, _abortHandlers.Token).AnyContext();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error processing message {MessageId} from {Source}", delivery.Message.MessageId, Source);
            await AbandonUnsettledAsync(delivery).AnyContext();
        }
        finally
        {
            delivery.HandlerReturned?.TrySetResult();
            await FinishAsync(delivery, !delivery.SlotsReleased).AnyContext();
        }
    }

    /// <summary>
    /// Abandons a delivery whose handling threw before it began to settle, so it is delivered again instead of staying
    /// unsettled: without lease expiration it would never come back, and a Kafka partition would stop committing behind it.
    /// </summary>
    private async Task AbandonUnsettledAsync(MessageDelivery delivery)
    {
        if (delivery.IsSettling || delivery.IsLeaseLost)
            return;

        try
        {
            if (await delivery.SettleAsync(static (receiver, message, ct) => receiver.AbandonAsync(message, TimeSpan.Zero, ct), "abandon", _logger).AnyContext() is null)
                _handler.OnReleased(delivery, "retry");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error abandoning message {MessageId} from {Source}", delivery.Message.MessageId, Source);
        }
    }

    /// <summary>Ends a delivery's time in flight once it is settled or released, and gives back whatever slot it still holds.</summary>
    private async Task FinishAsync(MessageDelivery delivery, bool holdsSlots)
    {
        await delivery.StopRenewingAsync().AnyContext();
        Leave(delivery);
        if (delivery.SettlesOffSlot)
            _settlements.Leave();

        if (!holdsSlots)
            _holds.Leave();
        else if (!delivery.SettlesOffSlot)
            ReleaseSlots(1);
    }

    /// <summary>A delivery is no longer in flight, on the processor or on its receiver.</summary>
    private void Leave(MessageDelivery delivery)
    {
        Interlocked.Decrement(ref _inFlight);
        if (delivery.ReceiverInFlight is { } receiverInFlight)
            Interlocked.Decrement(ref receiverInFlight.Value);
    }

    private Task<bool> WaitForInFlightAsync(TimeSpan timeout, CancellationToken cancellationToken)
        => WaitForNoneAsync(() => Volatile.Read(ref _inFlight), timeout, cancellationToken);

    private static async Task<bool> WaitForNoneAsync(Func<int> count, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (count() > 0)
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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using Foundatio.Extensions;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// Reads one source with its own consumer on a dedicated thread, which owns every call into the consumer: polling,
/// storing offsets, pausing and rebalance callbacks. Records are buffered until <see cref="ReceiveAsync"/> hands them
/// out; settlement only updates in-memory state under a lock, and the thread then stores the new commit offsets.
/// </summary>
internal sealed class KafkaMessageReceiver : IMessageReceiver
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FailedSettlementDelay = TimeSpan.FromSeconds(5);

    private readonly KafkaMessageTransport _transport;
    private readonly KafkaSourceTopology _topology;
    private readonly IConsumer<byte[]?, byte[]?> _consumer;
    private readonly ILogger _logger;
    private readonly int _bufferLimit;
    private readonly bool _ordered;
    private readonly object _lock = new();
    private readonly LinkedList<KafkaRecord> _buffer = new();
    private readonly Dictionary<TopicPartition, KafkaPartitionState> _partitions = [];
    private readonly Dictionary<string, int> _inFlightGroups = new(StringComparer.Ordinal);
    private readonly HashSet<TopicPartition> _paused = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _changed = NewSignal();
    private bool _offsetsChanged;
    private Exception? _failure;
    private int _started;
    private int _disposed;

    public KafkaMessageReceiver(KafkaMessageTransport transport, KafkaSourceTopology topology, ReceiverOptions options)
    {
        _transport = transport;
        _topology = topology;
        _logger = transport.Logger;
        _bufferLimit = Math.Max(32, options.PrefetchCount * 2);
        _ordered = topology.Source.Kind != MessageSourceKind.DeadLetter;

        var builder = new ConsumerBuilder<byte[]?, byte[]?>(transport.CreateConsumerConfig(topology.GroupId))
            .SetLogHandler((_, message) => transport.OnLog(message))
            .SetErrorHandler((_, error) => transport.OnError(error));

        if (!topology.IsInstance)
        {
            builder.SetPartitionsAssignedHandler((_, partitions) => OnAssigned(partitions))
                .SetPartitionsRevokedHandler((_, partitions) => Release(partitions.Select(p => p.TopicPartition).ToList(), _stopping.IsCancellationRequested ? TimeSpan.Zero : transport.Options.RebalanceGracePeriod))
                .SetPartitionsLostHandler((_, partitions) => OnLost(partitions.Select(p => p.TopicPartition).ToList()));
        }

        _consumer = builder.Build();
    }

    public MessageSource Source => _topology.Source;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return;

        new Thread(Run) { IsBackground = true, Name = $"Foundatio Kafka {Source}" }.Start();
    }

    /// <summary>Waits until the consumer has its first partition assignment (possibly empty) or has stopped.</summary>
    public async Task<bool> WaitUntilJoinedAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await _joined.Task.WaitAsync(timeout, cancellationToken).AnyContext();
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<IncomingMessage>> ReceiveAsync(int maxMessages, TimeSpan maxWait, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMessages, 1);
        int count = Math.Min(maxMessages, _transport.Capabilities.MaxReceiveBatchSize);
        long started = Stopwatch.GetTimestamp();

        while (true)
        {
            ThrowIfStopped();
            cancellationToken.ThrowIfCancellationRequested();

            var (messages, nextChange, changed) = Take(count);
            if (messages.Count > 0)
                return messages;

            var remaining = maxWait - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
                return [];

            var wait = nextChange is { } next && next < remaining ? next : remaining;
            using var delaySource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await Task.WhenAny(changed, Task.Delay(wait < TimeSpan.Zero ? TimeSpan.Zero : wait, delaySource.Token)).AnyContext();
            delaySource.Cancel();
        }
    }

    public Task CompleteAsync(IncomingMessage message, CancellationToken cancellationToken = default)
    {
        var delivery = GetDelivery(message);
        try
        {
            Finish(delivery);
            return Task.CompletedTask;
        }
        catch (MessageLeaseLostException ex)
        {
            return Task.FromException(ex);
        }
    }

    public async Task AbandonAsync(IncomingMessage message, TimeSpan delay, CancellationToken cancellationToken = default)
    {
        var delivery = GetDelivery(message);
        if (delay < TimeSpan.Zero)
            delay = TimeSpan.Zero;

        if (_topology.IsInstance || delivery.GroupKey is not null)
        {
            ReleaseLocally(delivery, delay);
            return;
        }

        lock (_lock)
            ThrowIfLost(delivery);

        try
        {
            await _transport.RepublishAsync(_topology, delivery, delay, cancellationToken).AnyContext();
        }
        catch (Exception ex) when (ex is not MessageLeaseLostException)
        {
            ReleaseAfterFailure(delivery, delay);
            throw;
        }

        Finish(delivery);
    }

    public async Task DeadLetterAsync(IncomingMessage message, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reason);
        if (_topology.IsInstance)
            throw new InvalidOperationException("Instance subscriptions do not have a dead-letter queue.");

        var delivery = GetDelivery(message);
        lock (_lock)
            ThrowIfLost(delivery);

        try
        {
            await _transport.DeadLetterAsync(_topology, delivery, reason, cancellationToken).AnyContext();
        }
        catch (Exception ex) when (ex is not MessageLeaseLostException)
        {
            ReleaseAfterFailure(delivery, FailedSettlementDelay);
            throw;
        }

        Finish(delivery);
    }

    public Task RenewAsync(IncomingMessage message, CancellationToken cancellationToken = default)
    {
        var delivery = GetDelivery(message);
        try
        {
            lock (_lock)
                ThrowIfLost(delivery);

            return Task.CompletedTask;
        }
        catch (MessageLeaseLostException ex)
        {
            return Task.FromException(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _transport.Remove(this);
        _stopping.Cancel();
        Signal();

        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            _consumer.Dispose();
            return;
        }

        try
        {
            await _stopped.Task.WaitAsync(StopTimeout).AnyContext();
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Timed out closing the Kafka consumer for {Source}", Source);
        }
    }

    private void Run()
    {
        try
        {
            if (_topology.IsInstance)
            {
                lock (_lock)
                {
                    foreach (var partition in _topology.Assignment)
                        _partitions[partition.TopicPartition] = new KafkaPartitionState(partition.TopicPartition);
                }

                _consumer.Assign(_topology.Assignment);
            }
            else
            {
                _consumer.Subscribe(_topology.Topics);
            }

            while (!_stopping.IsCancellationRequested)
            {
                Maintain();

                ConsumeResult<byte[]?, byte[]?>? result;
                try
                {
                    result = _consumer.Consume(PollTimeout);
                }
                catch (ConsumeException ex) when (!ex.Error.IsFatal)
                {
                    _logger.LogWarning(ex, "Error reading {Source}: {Reason}", Source, ex.Error.Reason);
                    continue;
                }

                if (result?.Message is not null)
                    Accept(result);
            }

            if (!_topology.IsInstance)
            {
                List<TopicPartition> owned;
                lock (_lock)
                    owned = _partitions.Keys.ToList();

                Release(owned, TimeSpan.Zero);
            }
        }
        catch (Exception ex)
        {
            _failure = ex;
            _logger.LogError(ex, "Kafka receiver for {Source} stopped: {Message}", Source, ex.Message);
        }
        finally
        {
            try
            {
                if (!_topology.IsInstance)
                    KafkaConsumerShutdown.LeaveGroup(_consumer);

                _consumer.Close();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error closing the Kafka consumer for {Source}", Source);
            }

            _consumer.Dispose();
            lock (_lock)
            {
                foreach (var state in _partitions.Values)
                    state.Revoked = true;

                _partitions.Clear();
                _buffer.Clear();
            }

            _joined.TrySetResult();
            _stopped.TrySetResult();
            Signal();
        }
    }

    /// <summary>Stores advanced commit offsets and pauses reading while the buffer is full.</summary>
    private void Maintain()
    {
        List<TopicPartitionOffset>? store = null;
        bool full, drained;
        lock (_lock)
        {
            if (_offsetsChanged && !_topology.IsInstance)
            {
                _offsetsChanged = false;
                foreach (var state in _partitions.Values)
                {
                    if (state.Revoked || state.CommitOffset is not { } offset || offset <= state.StoredOffset)
                        continue;

                    (store ??= []).Add(new TopicPartitionOffset(state.TopicPartition, offset));
                    state.StoredOffset = offset;
                }
            }

            // Records waiting for their delivery time stay in memory but do not stop the partition being read.
            int buffered = _buffer.Count;
            if (buffered >= _bufferLimit)
            {
                var now = _transport.TimeProvider.GetUtcNow();
                buffered -= _buffer.Count(r => r.AvailableAt > now);
            }

            full = buffered >= _bufferLimit;
            drained = buffered <= _bufferLimit / 2;
        }

        if (store is not null)
        {
            foreach (var offset in store)
            {
                try
                {
                    _consumer.StoreOffset(offset);
                }
                catch (KafkaException ex)
                {
                    _logger.LogDebug(ex, "Could not store offset {Offset} for {Source}", offset, Source);
                }
            }
        }

        if (full)
        {
            var pause = _consumer.Assignment.Where(p => !_paused.Contains(p)).ToList();
            if (pause.Count > 0)
            {
                _consumer.Pause(pause);
                _paused.UnionWith(pause);
            }
        }
        else if (drained && _paused.Count > 0)
        {
            var resume = _consumer.Assignment.Where(_paused.Contains).ToList();
            _paused.Clear();
            if (resume.Count > 0)
                _consumer.Resume(resume);
        }
    }

    private void Accept(ConsumeResult<byte[]?, byte[]?> result)
    {
        var record = KafkaMessageTransportCodec.Decode(result);
        lock (_lock)
        {
            if (!_partitions.TryGetValue(result.TopicPartition, out var state))
            {
                state = new KafkaPartitionState(result.TopicPartition);
                _partitions[result.TopicPartition] = state;
            }

            record.Partition = state;
            record.Offset = result.Offset.Value;
            state.Read(record.Offset);
            _buffer.AddLast(record);
        }

        Signal();
    }

    private (List<IncomingMessage> Messages, TimeSpan? NextChange, Task Changed) Take(int count)
    {
        var now = _transport.TimeProvider.GetUtcNow();
        lock (_lock)
        {
            var changed = _changed.Task;
            var messages = new List<IncomingMessage>();
            HashSet<string>? taken = null;
            HashSet<string>? held = null;
            DateTimeOffset? nextChange = null;

            // A group's records are handed out in buffer order, several per batch, but none while one handed out by
            // an earlier receive is unsettled, and none after a record of the group that has to wait.
            for (var node = _buffer.First; node is not null && messages.Count < count;)
            {
                var next = node.Next;
                var record = node.Value;
                var state = record.Partition!;
                string? group = _ordered ? record.GroupKey : null;

                bool available = !state.Revoking;
                if (group is not null && (held?.Contains(group) == true || _inFlightGroups.ContainsKey(group) && taken?.Contains(group) != true))
                    available = false;

                if (available && record.AvailableAt is { } availableAt && availableAt > now)
                {
                    available = false;
                    if (nextChange is null || availableAt < nextChange)
                        nextChange = availableAt;
                }

                if (!available && group is not null)
                    (held ??= new HashSet<string>(StringComparer.Ordinal)).Add(group);

                if (available)
                {
                    _buffer.Remove(node);
                    var delivery = new KafkaIncomingMessage
                    {
                        Owner = this,
                        Partition = state,
                        Offset = record.Offset,
                        MessageId = record.MessageId,
                        Body = record.Body,
                        Headers = record.Headers,
                        GroupKey = record.GroupKey,
                        DeliveryCount = record.DeliveryCount,
                        EnqueuedAt = record.EnqueuedAt
                    };

                    state.InFlight[record.Offset] = delivery;
                    if (group is not null)
                    {
                        _inFlightGroups[group] = _inFlightGroups.GetValueOrDefault(group) + 1;
                        (taken ??= new HashSet<string>(StringComparer.Ordinal)).Add(group);
                    }

                    messages.Add(delivery);
                }

                node = next;
            }

            return (messages, nextChange - now, changed);
        }
    }

    private void Finish(KafkaIncomingMessage delivery)
    {
        lock (_lock)
        {
            ThrowIfLost(delivery);
            delivery.Partition.InFlight.Remove(delivery.Offset);
            delivery.Partition.Pending.Remove(delivery.Offset);
            ReleaseGroup(delivery.GroupKey);
            _offsetsChanged = true;
        }

        Signal();
    }

    /// <summary>
    /// Puts a delivery back in the buffer ahead of the later records of its partition, so its group stays in order
    /// however its batch is released. The offset stays unfinished, so the message is read again from Kafka if the
    /// partition moves or the process stops.
    /// </summary>
    private void ReleaseLocally(KafkaIncomingMessage delivery, TimeSpan delay)
    {
        lock (_lock)
        {
            ThrowIfLost(delivery);
            delivery.Partition.InFlight.Remove(delivery.Offset);
            ReleaseGroup(delivery.GroupKey);
            var record = new KafkaRecord
            {
                MessageId = delivery.MessageId,
                Body = delivery.Body,
                Headers = delivery.Headers,
                GroupKey = delivery.GroupKey,
                DeliveryCount = delivery.DeliveryCount + 1,
                EnqueuedAt = delivery.EnqueuedAt,
                Partition = delivery.Partition,
                Offset = delivery.Offset,
                AvailableAt = delay > TimeSpan.Zero ? _transport.TimeProvider.GetUtcNow().Add(delay) : null
            };

            var later = _buffer.First;
            while (later is not null && !(later.Value.Partition == delivery.Partition && later.Value.Offset > delivery.Offset))
                later = later.Next;

            if (later is null)
                _buffer.AddLast(record);
            else
                _buffer.AddBefore(later, record);
        }

        Signal();
    }

    /// <summary>
    /// Leases never expire, so a delivery whose copy could not be written would otherwise stay unsettled until the
    /// partition moves. Hand it out again from memory instead.
    /// </summary>
    private void ReleaseAfterFailure(KafkaIncomingMessage delivery, TimeSpan delay)
    {
        try
        {
            ReleaseLocally(delivery, delay);
        }
        catch (MessageLeaseLostException)
        {
        }
    }

    private void OnAssigned(List<TopicPartition> partitions)
    {
        lock (_lock)
        {
            foreach (var partition in partitions)
                _partitions[partition] = new KafkaPartitionState(partition);
        }

        _paused.ExceptWith(partitions);
        _joined.TrySetResult();
        _logger.LogDebug("{Source} was assigned {Partitions}", Source, String.Join(", ", partitions));
    }

    /// <summary>
    /// Gives up partitions: waits up to <paramref name="gracePeriod"/> for handed-out messages to be settled, copies
    /// those still unsettled (without a group key) with an incremented delivery count, then commits.
    /// </summary>
    private void Release(List<TopicPartition> partitions, TimeSpan gracePeriod)
    {
        List<KafkaPartitionState> states;
        lock (_lock)
        {
            states = partitions.Select(p => _partitions.GetValueOrDefault(p)).OfType<KafkaPartitionState>().ToList();
            foreach (var state in states)
                state.Revoking = true;
        }

        _paused.ExceptWith(partitions);
        if (states.Count == 0)
            return;

        long started = Stopwatch.GetTimestamp();
        while (gracePeriod > TimeSpan.Zero && Stopwatch.GetElapsedTime(started) < gracePeriod)
        {
            lock (_lock)
            {
                if (states.All(s => s.InFlight.Count == 0))
                    break;
            }

            Thread.Sleep(10);
        }

        var unsettled = new List<KafkaIncomingMessage>();
        lock (_lock)
        {
            foreach (var state in states)
            {
                state.Revoked = true;
                _partitions.Remove(state.TopicPartition);
                foreach (var delivery in state.InFlight.Values)
                {
                    ReleaseGroup(delivery.GroupKey);
                    unsettled.Add(delivery);
                }

                state.InFlight.Clear();
            }

            for (var node = _buffer.First; node is not null;)
            {
                var next = node.Next;
                if (node.Value.Partition!.Revoked)
                    _buffer.Remove(node);

                node = next;
            }
        }

        Signal();

        foreach (var delivery in unsettled.Where(d => d.GroupKey is null))
        {
            try
            {
                if (_transport.RepublishAsync(_topology, delivery, TimeSpan.Zero, CancellationToken.None).Wait(ReleaseTimeout))
                {
                    lock (_lock)
                        delivery.Partition.Pending.Remove(delivery.Offset);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not release message {MessageId} from {Source}; it will be read again from offset {Offset}", delivery.MessageId, Source, delivery.Offset);
            }
        }

        List<TopicPartitionOffset> offsets;
        lock (_lock)
            offsets = states.Where(s => s.CommitOffset is not null).Select(s => new TopicPartitionOffset(s.TopicPartition, s.CommitOffset!.Value)).ToList();

        if (offsets.Count == 0)
            return;

        try
        {
            // Store before committing, so a later automatic commit of the offset store cannot move the offsets back.
            foreach (var offset in offsets)
                _consumer.StoreOffset(offset);

            // In a revoke callback the generation is still valid, so commit before the partitions move. When closing,
            // Close commits the stored offsets: a commit here could wait behind a JoinGroup the broker is holding.
            if (!_stopping.IsCancellationRequested)
                _consumer.Commit(offsets);

            _logger.LogDebug("{Source} released {Offsets}", Source, String.Join(", ", offsets));
        }
        catch (KafkaException ex)
        {
            _logger.LogWarning(ex, "Could not commit offsets for {Source} while releasing partitions: {Reason}", Source, ex.Error.Reason);
        }
    }

    private void OnLost(List<TopicPartition> partitions)
    {
        lock (_lock)
        {
            foreach (var partition in partitions)
            {
                if (!_partitions.Remove(partition, out var state))
                    continue;

                state.Revoked = true;
                foreach (var delivery in state.InFlight.Values)
                    ReleaseGroup(delivery.GroupKey);

                state.InFlight.Clear();
            }

            for (var node = _buffer.First; node is not null;)
            {
                var next = node.Next;
                if (node.Value.Partition!.Revoked)
                    _buffer.Remove(node);

                node = next;
            }
        }

        _paused.ExceptWith(partitions);
        _logger.LogWarning("{Source} lost {Partitions}; their unsettled messages will be delivered again", Source, String.Join(", ", partitions));
        Signal();
    }

    private KafkaIncomingMessage GetDelivery(IncomingMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message is not KafkaIncomingMessage delivery || delivery.Owner != this)
            throw new ArgumentException("The message was not received by this receiver.", nameof(message));

        return delivery;
    }

    private static void ThrowIfLost(KafkaIncomingMessage delivery)
    {
        if (delivery.Partition.Revoked || !delivery.Partition.InFlight.TryGetValue(delivery.Offset, out var current) || !ReferenceEquals(current, delivery))
            throw new MessageLeaseLostException($"The lease for message {delivery.MessageId} was lost: it was already settled or its partition moved to another receiver.");
    }

    private void ReleaseGroup(string? group)
    {
        if (group is null || !_inFlightGroups.TryGetValue(group, out int count))
            return;

        if (count <= 1)
            _inFlightGroups.Remove(group);
        else
            _inFlightGroups[group] = count - 1;
    }

    private void ThrowIfStopped()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        if (_failure is { } failure)
            throw new MessageReceiverFaultedException($"The Kafka receiver for {Source} stopped: {failure.Message}", failure);
    }

    private void Signal()
    {
        TaskCompletionSource previous;
        lock (_lock)
        {
            previous = _changed;
            _changed = NewSignal();
        }

        previous.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

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
/// <remarks>
/// <para>
/// While the buffer is full the thread stops reading and polls only every <see cref="KeepAliveInterval"/>, which
/// serves rebalances and keeps the member in its group; librdkafka's own prefetch queue, bounded by
/// <c>queued.max.messages.kbytes</c>, then stops fetching. Partitions are not paused for this: a paused partition
/// drops what librdkafka has fetched for it and takes about a second to deliver again once resumed. They are paused
/// only when the buffer passes twice its limit (handlers stalled for minutes, so polls kept adding a record each), and
/// when too many records wait for their delivery time, which only a partition being read further would add to.
/// </para>
/// <para>
/// Records that wait for their delivery time (delayed messages with a group key, and records released with a delay)
/// are kept apart from the buffer, ordered by when they are due. A group's later records on the same partition stay
/// in the buffer, held back, until the one that waits is due.
/// </para>
/// <para>
/// A queue or subscription receiver keeps what its partitions' committed offsets cannot say in the state topic
/// (<see cref="KafkaReceiverState"/>): delivery counts and retry times of records released in place, and, behind an
/// unfinished record, the offsets already finished. It writes a partition's state when it releases the partition, and
/// while it holds it soon after those change; the partition's next owner reads it before it hands anything out.
/// </para>
/// </remarks>
internal sealed class KafkaMessageReceiver : IMessageReceiver
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FailedSettlementDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StateWriteTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StateCheckInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FinishedWriteInterval = TimeSpan.FromSeconds(5);
    private const long BufferBytesLimit = 8 * 1024 * 1024;
    private const long TimedBytesLimit = 16 * 1024 * 1024;

    /// <summary>How many offsets must be finished behind an unfinished one before a live partition keeps them in the state topic.</summary>
    private const long FinishedWriteThreshold = 1000;

    private readonly KafkaMessageTransport _transport;
    private readonly KafkaSourceTopology _topology;
    private readonly IConsumer<byte[]?, byte[]?> _consumer;
    private readonly ILogger _logger;
    private readonly int _bufferLimit;
    private readonly int _timedLimit;
    private readonly bool _ordered;
    private readonly bool _stateful;
    private readonly object _lock = new();
    private readonly LinkedList<KafkaRecord> _buffer = new();
    private readonly PriorityQueue<KafkaRecord, (DateTimeOffset Due, long Sequence)> _timed = new();
    private readonly Dictionary<string, List<KafkaRecord>> _timedGroups = new(StringComparer.Ordinal);
    private readonly Dictionary<TopicPartition, KafkaPartitionState> _partitions = [];
    private readonly Dictionary<string, int> _inFlightGroups = new(StringComparer.Ordinal);
    private readonly HashSet<TopicPartition> _paused = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _changed = NewSignal();
    private long _bufferedBytes;
    private long _timedBytes;
    private long _timedSequence;
    private long _stateCheckedAt;
    private bool _offsetsChanged;
    private bool _pausesChanged;
    private bool _pauseAll;
    private bool _pauseTimed;
    private Exception? _failure;
    private int _pausedCount;
    private int _started;
    private int _disposed;

    public KafkaMessageReceiver(KafkaMessageTransport transport, KafkaSourceTopology topology, ReceiverOptions options)
    {
        _transport = transport;
        _topology = topology;
        _logger = transport.Logger;
        _bufferLimit = Math.Max(1, transport.Options.ReceiverBufferLimit ?? Math.Clamp(options.PrefetchCount * 8, 256, 4096));
        _timedLimit = Math.Max(1, transport.Options.ReceiverTimedLimit);
        _ordered = topology.Source.Kind != MessageSourceKind.DeadLetter;
        _stateful = topology.Source.Kind is MessageSourceKind.Queue or MessageSourceKind.Subscription;

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

    /// <summary>How many partitions the receiver has paused. For tests.</summary>
    internal int PausedCount => Volatile.Read(ref _pausedCount);

    /// <summary>How many records wait in memory for their delivery time. For tests.</summary>
    internal int TimedCount
    {
        get
        {
            lock (_lock)
                return _timed.Count;
        }
    }

    /// <summary>The partitions currently assigned to the consumer. For tests.</summary>
    internal List<TopicPartition> Assignment => _consumer.Assignment;

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

        if (ReleasesInPlace(delivery))
        {
            ReleaseLocally(delivery, delay);
            return;
        }

        var copied = BeginSettling(delivery);
        try
        {
            await _transport.RepublishAsync(_topology, delivery, delay, cancellationToken).AnyContext();
        }
        catch (Exception ex) when (ex is not MessageLeaseLostException)
        {
            copied.TrySetResult(false);
            ReleaseAfterFailure(delivery, delay);
            throw;
        }

        copied.TrySetResult(true);
        Finish(delivery, copied: true);
    }

    public async Task DeadLetterAsync(IncomingMessage message, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reason);
        if (_topology.IsInstance)
            throw new InvalidOperationException("Instance subscriptions do not have a dead-letter queue.");

        var delivery = GetDelivery(message);
        var copied = BeginSettling(delivery);
        try
        {
            await _transport.DeadLetterAsync(_topology, delivery, reason, cancellationToken).AnyContext();
        }
        catch (Exception ex) when (ex is not MessageLeaseLostException)
        {
            copied.TrySetResult(false);
            ReleaseAfterFailure(delivery, FailedSettlementDelay);
            throw;
        }

        copied.TrySetResult(true);
        Finish(delivery, copied: true);
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

            long lastPoll = Stopwatch.GetTimestamp();
            while (!_stopping.IsCancellationRequested)
            {
                var (full, changed) = Maintain();
                if (full)
                {
                    var sincePoll = Stopwatch.GetElapsedTime(lastPoll);
                    if (sincePoll < KeepAliveInterval)
                    {
                        changed.Wait(KeepAliveInterval - sincePoll);
                        continue;
                    }
                }

                ConsumeResult<byte[]?, byte[]?>? result;
                try
                {
                    result = _consumer.Consume(full ? TimeSpan.Zero : PollTimeout);
                }
                catch (ConsumeException ex) when (!ex.Error.IsFatal)
                {
                    _logger.LogWarning(ex, "Error reading {Source}: {Reason}", Source, ex.Error.Reason);
                    continue;
                }
                finally
                {
                    lastPoll = Stopwatch.GetTimestamp();
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
                DropRevokedRecords();
            }

            _joined.TrySetResult();
            _stopped.TrySetResult();
            Signal();
        }
    }

    /// <summary>
    /// Stores advanced commit offsets, moves records whose delivery time has come into the buffer, and pauses or
    /// resumes partitions. Returns whether the buffer is full, so nothing more should be read for now, and a task that
    /// completes when something changes.
    /// </summary>
    private (bool Full, Task Changed) Maintain()
    {
        List<TopicPartitionOffset>? store = null;
        List<TopicPartition>? timedPartitions = null;
        Task changed;
        bool full, overfull, timedOver, timedDrained, pausesChanged;
        lock (_lock)
        {
            changed = _changed.Task;
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

            PromoteDue(_transport.TimeProvider.GetUtcNow());
            full = _buffer.Count >= _bufferLimit || _bufferedBytes >= BufferBytesLimit;
            overfull = _buffer.Count >= _bufferLimit * 2 || _bufferedBytes >= BufferBytesLimit * 2;
            timedOver = _timed.Count >= _timedLimit || _timedBytes >= TimedBytesLimit;
            timedDrained = _timed.Count <= _timedLimit / 2 && _timedBytes <= TimedBytesLimit / 2;
            pausesChanged = _pausesChanged;
            _pausesChanged = false;
            if (timedOver || _pauseTimed)
                timedPartitions = _partitions.Values.Where(p => p.TimedCount > 0).Select(p => p.TopicPartition).ToList();
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

        if (_stateful && Stopwatch.GetElapsedTime(_stateCheckedAt) >= StateCheckInterval)
        {
            _stateCheckedAt = Stopwatch.GetTimestamp();
            WriteChangedStates();
        }

        bool pauseAll = overfull || _pauseAll && full;
        bool pauseTimed = timedOver || _pauseTimed && !timedDrained;
        if (pauseAll != _pauseAll || pauseTimed != _pauseTimed || pauseTimed && pausesChanged || pauseAll && pausesChanged)
        {
            _pauseAll = pauseAll;
            _pauseTimed = pauseTimed;
            UpdatePaused(timedPartitions);
        }

        return (full, changed);
    }

    /// <summary>Pauses what should not be read (every partition while the buffer is far over its limit, those holding timed records while too many are held) and resumes the rest.</summary>
    private void UpdatePaused(List<TopicPartition>? timedPartitions)
    {
        var wanted = new HashSet<TopicPartition>();
        if (_pauseAll)
            wanted.UnionWith(_consumer.Assignment);
        if (_pauseTimed && timedPartitions is not null)
            wanted.UnionWith(timedPartitions);

        var pause = wanted.Where(p => !_paused.Contains(p)).ToList();
        var resume = _paused.Where(p => !wanted.Contains(p)).ToList();
        if (pause.Count > 0)
        {
            _consumer.Pause(pause);
            _paused.UnionWith(pause);
            _logger.LogDebug("{Source} paused {Partitions}: {Buffered} records buffered, {Timed} waiting for their delivery time", Source, String.Join(", ", pause), _buffer.Count, _timed.Count);
        }

        if (resume.Count > 0)
        {
            _consumer.Resume(resume);
            _paused.ExceptWith(resume);
        }

        Volatile.Write(ref _pausedCount, _paused.Count);
    }

    private void Accept(ConsumeResult<byte[]?, byte[]?> result)
    {
        var record = KafkaMessageTransportCodec.Decode(result);
        var now = _transport.TimeProvider.GetUtcNow();
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
            Admit(record, now);
        }

        Signal();
    }

    /// <summary>
    /// Buffers a record read from Kafka, or holds it until its delivery time, after applying what the partition's
    /// previous owner kept about it: a record it finished is finished, and one it released keeps its count and retry time.
    /// </summary>
    private void Admit(KafkaRecord record, DateTimeOffset now)
    {
        var state = record.Partition!;
        if (!state.Loading && state.Recovered is { } recovered && Recover(state, recovered, record))
        {
            state.Pending.Remove(record.Offset);
            _offsetsChanged = true;
            return;
        }

        if (record.AvailableAt > now)
            AddTimed(record);
        else
            AddBuffered(record, last: true);
    }

    /// <summary>Applies a recovered state to a record; returns whether the record was finished already.</summary>
    private static bool Recover(KafkaPartitionState state, KafkaReceiverState recovered, KafkaRecord record)
    {
        if (!state.RecoveredAnchorSeen && recovered.Finished.Count > 0 && record.Offset >= recovered.AnchorOffset)
        {
            // Read from past the anchor, or a different record at its offset: the ranges cannot be shown to be this topic's.
            if (record.Offset == recovered.AnchorOffset && record.MessageId == recovered.AnchorMessageId)
                state.RecoveredAnchorSeen = true;
            else
                state.Recovered = recovered = recovered.WithoutFinished();
        }

        if (recovered.Attempts.TryGetValue(record.Offset, out var attempt) && attempt.MessageId == record.MessageId)
        {
            record.DeliveryCount = Math.Max(record.DeliveryCount, attempt.DeliveryCount);
            if (attempt.AvailableAt > record.AvailableAt || record.AvailableAt is null && attempt.AvailableAt is not null)
                record.AvailableAt = attempt.AvailableAt;

            record.Released = true;
            state.StateChanged = true;
        }

        bool finished = state.RecoveredAnchorSeen && recovered.IsFinished(record.Offset);
        if (record.Offset + 1 >= recovered.End)
            state.Recovered = null;

        return finished;
    }

    private (List<IncomingMessage> Messages, TimeSpan? NextChange, Task Changed) Take(int count)
    {
        var now = _transport.TimeProvider.GetUtcNow();
        lock (_lock)
        {
            var changed = _changed.Task;
            PromoteDue(now);
            var messages = new List<IncomingMessage>();
            HashSet<string>? taken = null;
            HashSet<string>? held = null;

            // A group's records are handed out in buffer order, several per batch, but none while one handed out by
            // an earlier receive is unsettled, and none after a record of the group that waits for its time.
            for (var node = _buffer.First; node is not null && messages.Count < count;)
            {
                var next = node.Next;
                var record = node.Value;
                var state = record.Partition!;
                string? group = _ordered ? record.GroupKey : null;

                bool available = !state.Revoking && !state.Loading;
                if (group is not null && (held?.Contains(group) == true || _inFlightGroups.ContainsKey(group) && taken?.Contains(group) != true || IsBehindTimed(record, group)))
                    available = false;

                if (!available && group is not null)
                    (held ??= new HashSet<string>(StringComparer.Ordinal)).Add(group);

                if (available)
                {
                    _buffer.Remove(node);
                    _bufferedBytes -= record.Size;
                    var delivery = new KafkaIncomingMessage
                    {
                        Owner = this,
                        Partition = state,
                        Offset = record.Offset,
                        Size = record.Size,
                        Released = record.Released,
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

            TimeSpan? nextChange = _timed.TryPeek(out _, out var first) ? first.Due - now : null;
            return (messages, nextChange, changed);
        }
    }

    /// <summary>Whether a record of an ordered group comes after one of its group, on its partition, that waits for its delivery time.</summary>
    private bool IsBehindTimed(KafkaRecord record, string group)
    {
        return _timedGroups.TryGetValue(group, out var waiting) && waiting.Exists(w => w.Partition == record.Partition && w.Offset < record.Offset);
    }

    /// <summary>Adds a record to the buffer: at the end, or ahead of the later records of its partition.</summary>
    private void AddBuffered(KafkaRecord record, bool last)
    {
        record.AvailableAt = null;
        _bufferedBytes += record.Size;
        var later = last ? null : _buffer.First;
        while (later is not null && !(later.Value.Partition == record.Partition && later.Value.Offset > record.Offset))
            later = later.Next;

        if (later is null)
            _buffer.AddLast(record);
        else
            _buffer.AddBefore(later, record);
    }

    private void AddTimed(KafkaRecord record)
    {
        _timed.Enqueue(record, (record.AvailableAt!.Value, _timedSequence++));
        _timedBytes += record.Size;
        if (record.Partition!.TimedCount++ == 0)
            _pausesChanged = true;

        if (_ordered && record.GroupKey is { } group)
        {
            if (!_timedGroups.TryGetValue(group, out var waiting))
                _timedGroups[group] = waiting = [];

            waiting.Add(record);
        }
    }

    /// <summary>Moves records whose delivery time has come into the buffer, ahead of the later records of their partition.</summary>
    private void PromoteDue(DateTimeOffset now)
    {
        while (_timed.TryPeek(out var record, out var due) && due.Due <= now)
        {
            _timed.Dequeue();
            ForgetTimed(record);
            if (!record.Partition!.Revoked)
                AddBuffered(record, last: false);
        }
    }

    private void ForgetTimed(KafkaRecord record)
    {
        _timedBytes -= record.Size;
        if (--record.Partition!.TimedCount == 0)
            _pausesChanged = true;

        if (_ordered && record.GroupKey is { } group && _timedGroups.TryGetValue(group, out var waiting))
        {
            waiting.Remove(record);
            if (waiting.Count == 0)
                _timedGroups.Remove(group);
        }
    }

    /// <summary>Drops the buffered and timed records of revoked partitions; they are read again by the partition's next owner.</summary>
    private void DropRevokedRecords()
    {
        for (var node = _buffer.First; node is not null;)
        {
            var next = node.Next;
            if (node.Value.Partition!.Revoked)
            {
                _buffer.Remove(node);
                _bufferedBytes -= node.Value.Size;
            }

            node = next;
        }

        if (!_timed.UnorderedItems.Any(t => t.Element.Partition!.Revoked))
            return;

        var kept = _timed.UnorderedItems.Where(t => !t.Element.Partition!.Revoked).ToList();
        foreach (var (record, _) in _timed.UnorderedItems.Where(t => t.Element.Partition!.Revoked).ToList())
            ForgetTimed(record);

        _timed.Clear();
        _timed.EnqueueRange(kept);
    }

    /// <summary>Records that a copy of the delivery is being written, so a release meanwhile does not copy it again.</summary>
    private TaskCompletionSource<bool> BeginSettling(KafkaIncomingMessage delivery)
    {
        var copied = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            ThrowIfLost(delivery);
            delivery.Settling = copied.Task;
        }

        return copied;
    }

    /// <summary>
    /// Finishes a delivery's offset. One whose copy was written (<paramref name="copied"/>) has been settled even if
    /// its partition moved meanwhile; the release then finished its offset if the copy was written in time.
    /// </summary>
    private void Finish(KafkaIncomingMessage delivery, bool copied = false)
    {
        lock (_lock)
        {
            if (copied && delivery.Partition.Revoked)
                return;

            ThrowIfLost(delivery);
            delivery.Partition.InFlight.Remove(delivery.Offset);
            delivery.Partition.Pending.Remove(delivery.Offset);
            ReleaseGroup(delivery.GroupKey);
            _offsetsChanged = true;
            if (delivery.Released)
                delivery.Partition.StateChanged = true;
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
                Size = delivery.Size,
                Released = true,
                Partition = delivery.Partition,
                Offset = delivery.Offset,
                AvailableAt = delay > TimeSpan.Zero ? _transport.TimeProvider.GetUtcNow().Add(delay) : null
            };

            delivery.Partition.StateChanged = true;

            if (record.AvailableAt is not null)
                AddTimed(record);
            else
                AddBuffered(record, last: false);
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
        var loading = new List<KafkaPartitionState>();
        lock (_lock)
        {
            foreach (var partition in partitions)
            {
                var state = new KafkaPartitionState(partition) { Loading = _stateful };
                _partitions[partition] = state;
                if (state.Loading)
                    loading.Add(state);
            }

            _pausesChanged = true;
        }

        _paused.ExceptWith(partitions);
        if (loading.Count > 0)
            _ = LoadStatesAsync(loading);
        else
            _joined.TrySetResult();

        _logger.LogDebug("{Source} was assigned {Partitions}", Source, String.Join(", ", partitions));
    }

    /// <summary>Reads the state the partitions' previous owners kept, then lets their records be handed out.</summary>
    private async Task LoadStatesAsync(List<KafkaPartitionState> states)
    {
        await Task.WhenAll(states.Select(LoadStateAsync)).AnyContext();
        _joined.TrySetResult();
    }

    private async Task LoadStateAsync(KafkaPartitionState state)
    {
        KafkaReceiverState? recovered = null;
        try
        {
            recovered = await _transport.LoadReceiverStateAsync(StateKey(state.TopicPartition), _stopping.Token).AnyContext();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the state of {Partition} for {Source}; delivering without it", state.TopicPartition, Source);
        }

        var now = _transport.TimeProvider.GetUtcNow();
        lock (_lock)
        {
            state.Loading = false;
            state.StateStored = recovered is not null;
            if (!state.Revoked && recovered is { IsEmpty: false })
            {
                state.Recovered = recovered;
                foreach (var record in TakeRecords(state).OrderBy(r => r.Offset))
                    Admit(record, now);
            }
        }

        Signal();
    }

    /// <summary>Removes and returns the buffered and timed records of a partition.</summary>
    private List<KafkaRecord> TakeRecords(KafkaPartitionState state)
    {
        var records = new List<KafkaRecord>();
        for (var node = _buffer.First; node is not null;)
        {
            var next = node.Next;
            if (node.Value.Partition == state)
            {
                _buffer.Remove(node);
                _bufferedBytes -= node.Value.Size;
                records.Add(node.Value);
            }

            node = next;
        }

        if (state.TimedCount > 0)
        {
            var timed = _timed.UnorderedItems.Where(t => t.Element.Partition == state).Select(t => t.Element).ToList();
            var kept = _timed.UnorderedItems.Where(t => t.Element.Partition != state).ToList();
            foreach (var record in timed)
                ForgetTimed(record);

            _timed.Clear();
            _timed.EnqueueRange(kept);
            records.AddRange(timed);
        }

        return records;
    }

    private string StateKey(TopicPartition partition) => $"{_topology.GroupId}\n{partition.Topic}\n{partition.Partition.Value}";

    /// <summary>
    /// The state to keep for a partition: the delivery counts of its records released in place (and of
    /// <paramref name="unsettled"/> deliveries, which count one more), what was recovered for records not read yet, and
    /// the offsets finished behind its lowest unfinished one.
    /// </summary>
    private static KafkaReceiverState BuildState(KafkaPartitionState state, List<KafkaRecord> records, IEnumerable<KafkaIncomingMessage> unsettled)
    {
        var attempts = new Dictionary<long, KafkaReceiverAttempt>();
        var recovered = state.Recovered;
        if (recovered is not null)
        {
            foreach (var (offset, attempt) in recovered.Attempts)
            {
                if (offset >= state.NextOffset)
                    attempts[offset] = attempt;
            }
        }

        var messageIds = state.InFlight.ToDictionary(d => d.Key, d => d.Value.MessageId);
        foreach (var record in records)
        {
            messageIds[record.Offset] = record.MessageId;
            if (record.Released)
                attempts[record.Offset] = new KafkaReceiverAttempt(record.MessageId, record.DeliveryCount, record.AvailableAt);
        }

        foreach (var delivery in unsettled)
        {
            messageIds[delivery.Offset] = delivery.MessageId;
            attempts[delivery.Offset] = new KafkaReceiverAttempt(delivery.MessageId, delivery.DeliveryCount + 1, null);
        }

        // Nothing read yet past the recovered anchor: keep its ranges as they were.
        if (recovered is { Finished.Count: > 0 } && !state.RecoveredAnchorSeen)
            return new KafkaReceiverState { AnchorOffset = recovered.AnchorOffset, AnchorMessageId = recovered.AnchorMessageId, Finished = recovered.Finished, Attempts = attempts };

        if (state.Pending.Count == 0 || !messageIds.TryGetValue(state.Pending.Min, out string? anchorId))
            return new KafkaReceiverState { Finished = [], Attempts = attempts };

        var finished = new List<(long Start, long End)>();
        long previous = state.Pending.Min;
        foreach (long offset in state.Pending)
        {
            if (offset > previous + 1)
                finished.Add((previous + 1, offset));

            previous = offset;
        }

        if (state.NextOffset > previous + 1)
            finished.Add((previous + 1, state.NextOffset));

        if (recovered is not null && state.RecoveredAnchorSeen)
        {
            foreach (var (start, end) in recovered.Finished)
            {
                if (end > state.NextOffset)
                    finished.Add((Math.Max(start, state.NextOffset), end));
            }
        }

        return new KafkaReceiverState { AnchorOffset = state.Pending.Min, AnchorMessageId = anchorId, Finished = finished, Attempts = attempts };
    }

    /// <summary>
    /// Writes the state of live partitions whose delivery counts changed, at most once per commit interval, and of those
    /// with many offsets finished behind an unfinished one, so a crash loses little of either.
    /// </summary>
    private void WriteChangedStates()
    {
        var writes = new List<(string Key, KafkaReceiverState State)>();
        lock (_lock)
        {
            foreach (var state in _partitions.Values)
            {
                if (state.Revoked || state.Revoking || state.Loading)
                    continue;

                var sinceWrite = Stopwatch.GetElapsedTime(state.StateWrittenAt);
                long finished = state.FinishedBehind;
                long anchor = state.Pending.Count > 0 ? state.Pending.Min : -1;
                var (writtenAnchor, writtenFinished) = state.StateWrittenRanges;
                bool rangesChanged = finished >= FinishedWriteThreshold && (anchor != writtenAnchor || finished - writtenFinished >= FinishedWriteThreshold) && sinceWrite >= FinishedWriteInterval;
                if (!(state.StateChanged && sinceWrite >= _transport.Options.CommitInterval) && !rangesChanged)
                    continue;

                var snapshot = BuildState(state, RecordsOf(state), []);
                if (snapshot.IsEmpty && !state.StateStored)
                {
                    state.StateChanged = false;
                    continue;
                }

                // Small ranges are not worth a write; a crash delivers them again, as it does whatever is not committed.
                if (finished < FinishedWriteThreshold && snapshot.Finished.Count > 0 && state.Recovered is null)
                    snapshot = snapshot.WithoutFinished();

                state.StateChanged = false;
                state.StateWrittenAt = Stopwatch.GetTimestamp();
                state.StateWrittenRanges = snapshot.Finished.Count > 0 ? (anchor, finished) : (-1, 0);
                state.StateStored = !snapshot.IsEmpty;
                writes.Add((StateKey(state.TopicPartition), snapshot));
            }
        }

        foreach (var (key, snapshot) in writes)
            _ = WriteStateAsync(key, snapshot);
    }

    private List<KafkaRecord> RecordsOf(KafkaPartitionState state)
    {
        var records = _buffer.Where(r => r.Partition == state).ToList();
        if (state.TimedCount > 0)
            records.AddRange(_timed.UnorderedItems.Select(t => t.Element).Where(r => r.Partition == state));

        return records;
    }

    private async Task<bool> WriteStateAsync(string key, KafkaReceiverState state)
    {
        try
        {
            await _transport.WriteReceiverStateAsync(key, state).AnyContext();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not keep the state of {Key} for {Source}", key.Replace('\n', '/'), Source);
            return false;
        }
    }

    /// <summary>
    /// Gives up partitions: waits up to <paramref name="gracePeriod"/> for handed-out messages to be settled, copies
    /// those still unsettled (unless they are released in place) with an incremented delivery count, then commits.
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

        ForgetPaused(partitions);
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
        var records = new Dictionary<KafkaPartitionState, List<KafkaRecord>>();
        lock (_lock)
        {
            foreach (var state in states)
            {
                if (_stateful)
                    records[state] = RecordsOf(state);

                state.Revoked = true;
                _partitions.Remove(state.TopicPartition);
                foreach (var delivery in state.InFlight.Values)
                {
                    ReleaseGroup(delivery.GroupKey);
                    unsettled.Add(delivery);
                }

                state.InFlight.Clear();
            }

            DropRevokedRecords();
        }

        Signal();
        var copied = CopyUnsettled(unsettled);
        if (_stateful)
            WriteReleasedStates(states, records, unsettled.Where(d => !copied.Contains(d)).ToList());

        List<TopicPartitionOffset> offsets;
        lock (_lock)
            offsets = states.Where(s => s.CommitOffset is not null).Select(s => new TopicPartitionOffset(s.TopicPartition, s.CommitOffset!.Value)).ToList();

        // librdkafka commits the stored offsets of partitions it unassigns, while the generation is still valid, and
        // Close commits them when stopping. A synchronous commit here could block the rebalance without a bound.
        foreach (var offset in offsets)
        {
            try
            {
                _consumer.StoreOffset(offset);
            }
            catch (KafkaException ex)
            {
                _logger.LogWarning(ex, "Could not store offset {Offset} for {Source} while releasing partitions: {Reason}", offset, Source, ex.Error.Reason);
            }
        }

        if (offsets.Count > 0)
            _logger.LogDebug("{Source} released {Offsets}", Source, String.Join(", ", offsets));
    }

    /// <summary>
    /// Copies released deliveries that are not released in place to the retry topic, all at once and within one
    /// <see cref="ReleaseTimeout"/>, and finishes the offsets of those copied. A delivery whose abandon or dead-letter
    /// copy is still being written is not copied again; its offset is finished if that copy is written in time.
    /// </summary>
    private HashSet<KafkaIncomingMessage> CopyUnsettled(List<KafkaIncomingMessage> unsettled)
    {
        var done = new HashSet<KafkaIncomingMessage>();
        var copies = new List<(KafkaIncomingMessage Delivery, Task<bool> Copied)>();
        foreach (var delivery in unsettled)
        {
            if (delivery.Settling is { } settling)
                copies.Add((delivery, settling));
            else if (!ReleasesInPlace(delivery))
                copies.Add((delivery, CopyAsync(delivery)));
        }

        if (copies.Count == 0)
            return done;

        try
        {
            Task.WhenAll(copies.Select(c => c.Copied)).Wait(ReleaseTimeout);
        }
        catch (AggregateException)
        {
        }

        int failed = 0;
        lock (_lock)
        {
            foreach (var (delivery, copied) in copies)
            {
                if (copied.IsCompletedSuccessfully && copied.Result)
                {
                    delivery.Partition.Pending.Remove(delivery.Offset);
                    done.Add(delivery);
                }
                else
                {
                    failed++;
                }
            }
        }

        if (failed > 0)
            _logger.LogWarning("Could not release {Count} messages from {Source} in time; they will be read again from their offsets", failed, Source);

        return done;
    }

    /// <summary>Writes the state of released partitions for their next owners, waiting at most <see cref="StateWriteTimeout"/>.</summary>
    private void WriteReleasedStates(List<KafkaPartitionState> states, Dictionary<KafkaPartitionState, List<KafkaRecord>> records, List<KafkaIncomingMessage> unsettled)
    {
        var writes = new List<Task<bool>>();
        lock (_lock)
        {
            foreach (var state in states)
            {
                if (state.Loading)
                    continue;

                var snapshot = BuildState(state, records.GetValueOrDefault(state) ?? [], unsettled.Where(d => d.Partition == state));
                if (snapshot.IsEmpty && !state.StateStored)
                    continue;

                writes.Add(WriteStateAsync(StateKey(state.TopicPartition), snapshot));
            }
        }

        if (writes.Count > 0 && !Task.WhenAll(writes).Wait(StateWriteTimeout))
            _logger.LogWarning("Could not keep the state of the partitions {Source} released in time", Source);
    }

    private async Task<bool> CopyAsync(KafkaIncomingMessage delivery)
    {
        try
        {
            await _transport.RepublishAsync(_topology, delivery, TimeSpan.Zero, CancellationToken.None).AnyContext();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not release message {MessageId} from {Source}", delivery.MessageId, Source);
            return false;
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

            DropRevokedRecords();
        }

        ForgetPaused(partitions);
        _logger.LogWarning("{Source} lost {Partitions}; their unsettled messages will be delivered again", Source, String.Join(", ", partitions));
        Signal();
    }

    /// <summary>
    /// librdkafka keeps a partition paused after it is revoked, and a partition assigned back to the same consumer
    /// would stay paused, so resume it while it is still assigned.
    /// </summary>
    private void ForgetPaused(List<TopicPartition> partitions)
    {
        var paused = partitions.Where(_paused.Remove).ToList();
        if (paused.Count > 0)
            _consumer.Resume(paused);

        Volatile.Write(ref _pausedCount, _paused.Count);
    }

    private KafkaIncomingMessage GetDelivery(IncomingMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message is not KafkaIncomingMessage delivery || delivery.Owner != this)
            throw new ArgumentException("The message was not received by this receiver.", nameof(message));

        return delivery;
    }

    /// <summary>
    /// Whether an abandoned or released delivery stays at its offset instead of being copied to the retry topic: on
    /// instance subscriptions, which have none; for groups, to keep them in order; and for dead letters, so that
    /// peeking at them leaves them where they are.
    /// </summary>
    private bool ReleasesInPlace(KafkaIncomingMessage delivery)
    {
        return _topology.IsInstance || _topology.Source.Kind == MessageSourceKind.DeadLetter || delivery.GroupKey is not null;
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

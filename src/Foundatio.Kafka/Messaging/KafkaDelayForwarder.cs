using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using Foundatio.Extensions;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// Moves delayed records out of the delay tier topics when they are due. All processes with a receiver share one
/// consumer group for the tiers. A record is due in its tier at its append time plus the tier's delay; until then
/// its partition is paused with the record held. A due record goes to its target when its delivery time has come,
/// to a shorter tier when much of the delay remains, or is held in memory for the last fraction of the smallest tier.
/// The forwarder writes on producers of its own, so a backlog it forwards never fills the queue the application
/// sends on, and it stops reading while too many of its records are in flight.
/// </summary>
internal sealed class KafkaDelayForwarder : IAsyncDisposable
{
    private static readonly TimeSpan MaxPollTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HandOffTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A record only gets into a tier after its target and the tiers were created, so a topic the forwarder cannot
    /// find was deleted. librdkafka waits 30 seconds by default before it fails a send to a topic it cannot find.
    /// </summary>
    private const int MissingTopicTimeoutMs = 1000;

    private const long MaxInFlightBytes = 64 * 1024 * 1024;

    private readonly KafkaMessageTransport _transport;
    private readonly IReadOnlyDictionary<string, TimeSpan> _tiers;
    private readonly IConsumer<byte[]?, byte[]?> _consumer;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly Dictionary<TopicPartition, TierPartition> _partitions = [];
    private readonly Dictionary<TopicPartition, Waiting> _paused = [];
    private readonly HashSet<TopicPartition> _throttled = [];
    private readonly List<Waiting> _scheduled = [];
    private readonly ConcurrentQueue<Waiting> _retries = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeSpan _retryDelay;
    private readonly int _maxAttempts;
    private readonly int _maxInFlight;
    private readonly IProducer<byte[]?, byte[]> _producer;
    private IProducer<byte[]?, byte[]>? _isolatedProducer;
    private int _inFlight;
    private long _inFlightBytes;
    private bool _throttling;
    private bool _offsetsChanged;
    private string[] _heldTopics = [];
    private Exception? _injectedFailure;
    private int _failed;
    private int _disposed;

    public KafkaDelayForwarder(KafkaMessageTransport transport, IReadOnlyDictionary<string, TimeSpan> tiers)
    {
        _transport = transport;
        _tiers = tiers;
        _logger = transport.Logger;
        _retryDelay = transport.Options.DelayForwardRetryDelay;
        _maxAttempts = Math.Max(1, transport.Options.DelayForwardMaxAttempts);
        _maxInFlight = Math.Max(1, transport.Options.DelayForwardMaxInFlight);
        _producer = CreateProducer();
        _consumer = new ConsumerBuilder<byte[]?, byte[]?>(transport.CreateConsumerConfig(transport.Names.DelayGroup))
            .SetLogHandler((_, message) => transport.OnLog(message))
            .SetErrorHandler((_, error) => transport.OnError(error))
            .SetPartitionsAssignedHandler((_, partitions) => OnAssigned(partitions))
            .SetPartitionsRevokedHandler((_, partitions) => OnRevoked(partitions.Select(p => p.TopicPartition).ToList(), commit: true))
            .SetPartitionsLostHandler((_, partitions) => OnRevoked(partitions.Select(p => p.TopicPartition).ToList(), commit: false))
            .Build();
    }

    public void Start() => new Thread(Run) { IsBackground = true, Name = "Foundatio Kafka delay forwarder" }.Start();

    /// <summary>Completes when the forwarder's thread has stopped, because it was disposed or because it failed.</summary>
    public Task Completion => _stopped.Task;

    /// <summary>The forwarder's thread stopped on an error rather than because the forwarder was disposed.</summary>
    public bool Failed => Volatile.Read(ref _failed) == 1;

    /// <summary>The tier topics whose partitions are paused holding a record that is not due yet. For tests.</summary>
    internal IReadOnlyList<string> HeldTopics => Volatile.Read(ref _heldTopics);

    /// <summary>The tier partitions currently assigned to the consumer. For tests.</summary>
    internal List<TopicPartition> Assignment => _consumer.Assignment;

    /// <summary>How many records read from the tiers are not finished yet. For tests.</summary>
    internal int PendingCount
    {
        get
        {
            lock (_lock)
                return _partitions.Values.Sum(p => p.Pending.Count);
        }
    }

    /// <summary>Makes the forwarder's thread fail, as a fatal consumer error would. For tests.</summary>
    internal void FailForTesting(Exception exception) => Volatile.Write(ref _injectedFailure, exception);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _stopping.Cancel();
        try
        {
            await _stopped.Task.WaitAsync(StopTimeout).AnyContext();
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Timed out closing the Kafka delay forwarder");
        }
    }

    private void Run()
    {
        try
        {
            _consumer.Subscribe(_tiers.Keys);
            while (!_stopping.IsCancellationRequested)
            {
                if (Volatile.Read(ref _injectedFailure) is { } failure)
                    throw failure;

                StoreOffsets();
                Throttle();
                var now = _transport.TimeProvider.GetUtcNow();
                var nextDue = ProcessWaiting(now);

                var timeout = nextDue is { } due && due - now < MaxPollTimeout ? due - now : MaxPollTimeout;
                ConsumeResult<byte[]?, byte[]?>? result;
                try
                {
                    result = _consumer.Consume(timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout);
                }
                catch (ConsumeException ex) when (!ex.Error.IsFatal)
                {
                    _logger.LogWarning(ex, "Error reading the delay topics: {Reason}", ex.Error.Reason);
                    continue;
                }

                if (result?.Message is not null)
                    Accept(result);
            }
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _failed, 1);
            _logger.LogError(ex, "Kafka delay forwarder stopped: {Message}", ex.Message);
        }
        finally
        {
            try
            {
                KafkaConsumerShutdown.LeaveGroup(_consumer);
                _consumer.Close();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error closing the Kafka delay forwarder");
            }

            _consumer.Dispose();
            _producer.Flush(DrainTimeout);
            _producer.Dispose();
            if (_isolatedProducer is { } isolated)
            {
                isolated.Flush(DrainTimeout);
                isolated.Dispose();
            }

            _stopped.TrySetResult();
        }
    }

    private void Accept(ConsumeResult<byte[]?, byte[]?> result)
    {
        var partition = result.TopicPartition;
        if (_paused.ContainsKey(partition))
        {
            // Read after the partition was paused: read it again once the held record has gone.
            _consumer.Seek(result.TopicPartitionOffset);
            return;
        }

        if (_throttling)
        {
            // Read before the partition was paused, or assigned since: read it again once records in flight finish.
            _consumer.Pause([partition]);
            _consumer.Seek(result.TopicPartitionOffset);
            _throttled.Add(partition);
            return;
        }

        TierPartition state;
        lock (_lock)
        {
            if (!_partitions.TryGetValue(partition, out state!))
            {
                state = new TierPartition(partition);
                _partitions[partition] = state;
            }
        }

        var tier = _tiers.GetValueOrDefault(result.Topic);
        var appendedAt = result.Message.Timestamp.Type == TimestampType.NotAvailable ? _transport.TimeProvider.GetUtcNow() : new DateTimeOffset(result.Message.Timestamp.UtcDateTime);
        var waiting = new Waiting(result, state, appendedAt.Add(tier));
        if (waiting.Due > _transport.TimeProvider.GetUtcNow())
        {
            _paused[partition] = waiting;
            _consumer.Pause([partition]);
            PublishHeld();
            return;
        }

        lock (_lock)
            state.Read(result);

        Route(waiting);
    }

    /// <summary>Handles held and scheduled records that are due; returns when the next one is due.</summary>
    private DateTimeOffset? ProcessWaiting(DateTimeOffset now)
    {
        while (_retries.TryDequeue(out var retry))
            _scheduled.Add(retry with { Due = now.Add(_retryDelay) });

        DateTimeOffset? nextDue = null;
        foreach (var (partition, waiting) in _paused.ToList())
        {
            if (waiting.Due > now)
            {
                nextDue = nextDue is null || waiting.Due < nextDue ? waiting.Due : nextDue;
                continue;
            }

            _paused.Remove(partition);
            lock (_lock)
                waiting.State.Read(waiting.Result);

            Route(waiting);
            if (_throttling)
                _throttled.Add(partition);
            else
                _consumer.Resume([partition]);

            PublishHeld();
        }

        for (int i = _scheduled.Count - 1; i >= 0; i--)
        {
            var waiting = _scheduled[i];
            if (waiting.Due > now)
            {
                nextDue = nextDue is null || waiting.Due < nextDue ? waiting.Due : nextDue;
                continue;
            }

            _scheduled.RemoveAt(i);
            if (!waiting.State.Revoked)
                Route(waiting);
        }

        return nextDue;
    }

    /// <summary>Sends a due record on: to its target, to a shorter tier, or into memory for the last part of its delay.</summary>
    private void Route(Waiting waiting)
    {
        var result = waiting.Result;
        string? target = KafkaMessageTransportCodec.GetHeader(result, KafkaMessageTransportCodec.TargetHeader);
        if (target is null)
        {
            _logger.LogWarning("Dropping record {Offset} without a target from {Topic}", result.TopicPartitionOffset, result.Topic);
            Done(waiting);
            return;
        }

        // Anyone who can write to the delay topics could name any target, and the forwarder writes with the
        // application's credentials, so only the transport's own topics are forwarded to.
        if (!_transport.Names.IsDelayTarget(target))
        {
            _logger.LogWarning("Dropping delayed record {Offset}: its target {Target} is not one of this transport's topics", result.TopicPartitionOffset, target);
            Done(waiting);
            return;
        }

        var now = _transport.TimeProvider.GetUtcNow();
        var remaining = (KafkaMessageTransportCodec.GetTime(result, KafkaMessageTransportCodec.DeliverAtHeader) ?? now) - now;
        if (remaining <= TimeSpan.Zero)
        {
            Produce(target, KafkaMessageTransportCodec.WithoutDelay(result), waiting, isTarget: true);
        }
        else if (remaining < KafkaDelayTiers.Smallest)
        {
            _scheduled.Add(waiting with { Due = now.Add(remaining) });
        }
        else
        {
            Produce(_transport.Names.Delay(KafkaDelayTiers.Choose(remaining)), KafkaMessageTransportCodec.Copy(result), waiting, isTarget: false);
        }
    }

    private void Produce(string topic, Message<byte[]?, byte[]> message, Waiting waiting, bool isTarget)
    {
        long size = message.Value.Length + (message.Key?.Length ?? 0);
        Interlocked.Increment(ref _inFlight);
        Interlocked.Add(ref _inFlightBytes, size);
        try
        {
            var producer = waiting.Isolated ? GetIsolatedProducer() : _producer;
            producer.Produce(topic, message, report =>
            {
                Release(size);
                OnDelivered(report.Error, topic, waiting, isTarget);
            });
        }
        catch (ProduceException<byte[]?, byte[]> ex)
        {
            Release(size);
            OnDelivered(ex.Error, topic, waiting, isTarget);
        }
        catch (Exception ex)
        {
            Release(size);
            _logger.LogWarning(ex, "Could not forward delayed record {Offset} to {Topic}; retrying", waiting.Result.TopicPartitionOffset, topic);
            _retries.Enqueue(waiting);
        }
    }

    private void Release(long size)
    {
        Interlocked.Decrement(ref _inFlight);
        Interlocked.Add(ref _inFlightBytes, -size);
    }

    /// <summary>
    /// Stops reading the tiers while too many forwarded records are in flight (a slow broker, or a backlog that came
    /// due at once), and reads them again once half of those have finished.
    /// </summary>
    private void Throttle()
    {
        int inFlight = Volatile.Read(ref _inFlight);
        long inFlightBytes = Interlocked.Read(ref _inFlightBytes);
        if (!_throttling)
        {
            if (inFlight < _maxInFlight && inFlightBytes < MaxInFlightBytes)
                return;

            _throttling = true;
            var partitions = _consumer.Assignment.Where(p => !_paused.ContainsKey(p)).ToList();
            _consumer.Pause(partitions);
            _throttled.UnionWith(partitions);
            _logger.LogDebug("Pausing the delay topics while {Count} forwarded records are in flight", inFlight);
            return;
        }

        if (inFlight > _maxInFlight / 2 || inFlightBytes > MaxInFlightBytes / 2)
            return;

        _throttling = false;
        _consumer.Resume(_throttled.ToList());
        _throttled.Clear();
    }

    /// <summary>
    /// Finishes a forwarded record, or retries it. A target that keeps rejecting a record on its own (too large for
    /// the topic, not authorized) would pin its tier partition's committed offset forever, so the record is dropped
    /// after a bounded number of attempts. Errors that may pass, and failures to move a record to another tier, are
    /// retried for as long as they last.
    /// </summary>
    private void OnDelivered(Error error, string topic, Waiting waiting, bool isTarget)
    {
        if (!error.IsError)
        {
            Done(waiting);
            return;
        }

        var offset = waiting.Result.TopicPartitionOffset;
        if (error.Code is ErrorCode.UnknownTopicOrPart or ErrorCode.Local_UnknownTopic)
        {
            // The destination was deleted (DeleteAsync) while the message waited.
            _logger.LogInformation("Dropping delayed record {Offset}: its destination {Topic} no longer exists", offset, topic);
            Done(waiting);
            return;
        }

        if (!isTarget || !IsPermanent(error.Code))
        {
            _logger.LogWarning("Could not forward delayed record {Offset} to {Topic}: {Reason}; retrying", offset, topic, error.Reason);
            _retries.Enqueue(waiting);
            return;
        }

        // A batch fails as a whole, so only an attempt made alone shows that the record itself is rejected.
        int attempts = waiting.Isolated ? waiting.Attempts + 1 : 0;
        if (attempts >= _maxAttempts)
        {
            _logger.LogError("Dropping delayed record {Offset} for message {MessageId}: {Topic} rejected it {Attempts} times: {Reason}",
                offset, KafkaMessageTransportCodec.GetHeader(waiting.Result, KafkaMessageTransportCodec.IdHeader), topic, attempts, error.Reason);
            Done(waiting);
            return;
        }

        _logger.LogWarning("{Topic} rejected delayed record {Offset}: {Reason}; retrying it on its own", topic, offset, error.Reason);
        _retries.Enqueue(waiting with { Attempts = attempts, Isolated = true });
    }

    private static bool IsPermanent(ErrorCode code)
    {
        return code is ErrorCode.MsgSizeTooLarge or ErrorCode.InvalidMsgSize or ErrorCode.RecordListTooLarge
            or ErrorCode.InvalidRecord or ErrorCode.InvalidTimestamp or ErrorCode.UnsupportedForMessageFormat or ErrorCode.PolicyViolation
            or ErrorCode.TopicAuthorizationFailed or ErrorCode.ClusterAuthorizationFailed or ErrorCode.TopicException;
    }

    /// <summary>A producer that sends every record in a batch of its own, for retrying records a target rejected.</summary>
    private IProducer<byte[]?, byte[]> GetIsolatedProducer()
    {
        return _isolatedProducer ??= CreateProducer(c =>
        {
            c.BatchNumMessages = 1;
            c.LingerMs = 0;
        });
    }

    private IProducer<byte[]?, byte[]> CreateProducer(Action<ProducerConfig>? adjust = null)
    {
        return _transport.CreateProducer(c =>
        {
            c.TopicMetadataPropagationMaxMs = MissingTopicTimeoutMs;
            adjust?.Invoke(c);
        });
    }

    private void PublishHeld() => Volatile.Write(ref _heldTopics, _paused.Keys.Select(p => p.Topic).ToArray());

    private void Done(Waiting waiting)
    {
        lock (_lock)
        {
            waiting.State.Finish(waiting.Result.Offset.Value);
            _offsetsChanged = true;
        }
    }

    private void StoreOffsets()
    {
        List<TopicPartitionOffset>? store = null;
        lock (_lock)
        {
            if (!_offsetsChanged)
                return;

            _offsetsChanged = false;
            foreach (var state in _partitions.Values)
            {
                if (state.Revoked || state.CommitOffset is not { } offset || offset <= state.StoredOffset)
                    continue;

                (store ??= []).Add(new TopicPartitionOffset(state.TopicPartition, offset));
                state.StoredOffset = offset;
            }
        }

        foreach (var offset in store ?? [])
        {
            try
            {
                _consumer.StoreOffset(offset);
            }
            catch (KafkaException ex)
            {
                _logger.LogDebug(ex, "Could not store delay offset {Offset}", offset);
            }
        }
    }

    private void OnAssigned(List<TopicPartition> partitions)
    {
        lock (_lock)
        {
            foreach (var partition in partitions)
                _partitions[partition] = new TierPartition(partition);
        }
    }

    /// <summary>
    /// Gives up partitions. A record the forwarder has not finished would hold the committed offset back, and the
    /// partition's next owner would forward again every record after it, so what is still unfinished once the records
    /// in flight had their time is copied back to the tiers and the offset committed past it. A record whose forward
    /// was only slow is then delivered twice; one that pinned the offset would have had every record behind it delivered again.
    /// </summary>
    private void OnRevoked(List<TopicPartition> partitions, bool commit)
    {
        // librdkafka keeps a revoked partition paused, so one assigned back would never be read again.
        var revoked = partitions.ToHashSet();
        var paused = partitions.Where(p => _paused.Remove(p) | _throttled.Remove(p)).ToList();
        if (paused.Count > 0)
        {
            _consumer.Resume(paused);
            PublishHeld();
        }

        _scheduled.RemoveAll(w => revoked.Contains(w.State.TopicPartition));
        List<TierPartition> states = [];
        lock (_lock)
        {
            foreach (var partition in partitions)
            {
                if (!_partitions.Remove(partition, out var state))
                    continue;

                state.Revoked = true;
                states.Add(state);
            }
        }

        if (!commit)
            return;

        if (UnfinishedCount(states) > 0)
        {
            _producer.Flush(DrainTimeout);
            _isolatedProducer?.Flush(DrainTimeout);
            HandOff(states);
        }

        List<TopicPartitionOffset> offsets = [];
        lock (_lock)
        {
            foreach (var state in states)
            {
                if (state.CommitOffset is { } offset)
                    offsets.Add(new TopicPartitionOffset(state.TopicPartition, offset));
            }
        }

        // librdkafka commits the stored offsets of partitions it unassigns, and Close those of the rest; a synchronous
        // commit here could block the rebalance without a bound.
        foreach (var offset in offsets)
        {
            try
            {
                _consumer.StoreOffset(offset);
            }
            catch (KafkaException ex)
            {
                _logger.LogWarning(ex, "Could not store delay offset {Offset} while releasing partitions: {Reason}", offset, ex.Error.Reason);
            }
        }
    }

    private int UnfinishedCount(List<TierPartition> states)
    {
        lock (_lock)
            return states.Sum(s => s.Pending.Count);
    }

    /// <summary>Copies the unfinished records of revoked partitions back to the tiers, finishing each copy written.</summary>
    private void HandOff(List<TierPartition> states)
    {
        List<(TierPartition State, ConsumeResult<byte[]?, byte[]?> Result)> unfinished;
        lock (_lock)
            unfinished = [.. states.SelectMany(s => s.Unfinished.Select(r => (s, r)))];

        if (unfinished.Count == 0)
            return;

        var now = _transport.TimeProvider.GetUtcNow();
        int handedOff = 0;
        foreach (var (state, result) in unfinished)
        {
            var remaining = (KafkaMessageTransportCodec.GetTime(result, KafkaMessageTransportCodec.DeliverAtHeader) ?? now) - now;
            string tier = _transport.Names.Delay(KafkaDelayTiers.Choose(remaining));
            try
            {
                _producer.Produce(tier, KafkaMessageTransportCodec.Copy(result), report =>
                {
                    if (report.Error.IsError)
                        return;

                    lock (_lock)
                        state.Finish(result.Offset.Value);

                    Interlocked.Increment(ref handedOff);
                });
            }
            catch (KafkaException ex)
            {
                _logger.LogDebug(ex, "Could not hand delayed record {Offset} back to {Topic}", result.TopicPartitionOffset, tier);
            }
        }

        _producer.Flush(HandOffTimeout);
        _logger.LogInformation("Handed {HandedOff} of {Count} unfinished delayed records back to the delay topics while releasing partitions",
            Volatile.Read(ref handedOff), unfinished.Count);
    }

    /// <summary>A record read from a tier and not finished, with the attempts its target rejected it alone.</summary>
    private sealed record Waiting(ConsumeResult<byte[]?, byte[]?> Result, TierPartition State, DateTimeOffset Due)
    {
        public int Attempts { get; init; }

        public bool Isolated { get; init; }
    }

    /// <summary>
    /// The records read from one assigned tier partition and not finished. The committed offset is the lowest
    /// unfinished one. A new instance is created for every assignment. Guarded by the forwarder's lock.
    /// </summary>
    private sealed class TierPartition(TopicPartition topicPartition)
    {
        private readonly Dictionary<long, ConsumeResult<byte[]?, byte[]?>> _records = [];

        public TopicPartition TopicPartition { get; } = topicPartition;

        public SortedSet<long> Pending { get; } = [];

        public IEnumerable<ConsumeResult<byte[]?, byte[]?>> Unfinished => _records.Values;

        /// <summary>The offset after the last one read, or -1 before the first read.</summary>
        public long NextOffset { get; private set; } = -1;

        /// <summary>The last offset passed to the consumer's offset store, or -1.</summary>
        public long StoredOffset { get; set; } = -1;

        public bool Revoked { get; set; }

        public long? CommitOffset => Pending.Count > 0 ? Pending.Min : NextOffset >= 0 ? NextOffset : null;

        public void Read(ConsumeResult<byte[]?, byte[]?> result)
        {
            long offset = result.Offset.Value;
            Pending.Add(offset);
            _records[offset] = result;
            if (offset >= NextOffset)
                NextOffset = offset + 1;
        }

        public void Finish(long offset)
        {
            Pending.Remove(offset);
            _records.Remove(offset);
        }
    }
}

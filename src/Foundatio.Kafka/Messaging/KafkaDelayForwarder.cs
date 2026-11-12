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
/// </summary>
internal sealed class KafkaDelayForwarder : IAsyncDisposable
{
    private static readonly TimeSpan MaxPollTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);

    private readonly KafkaMessageTransport _transport;
    private readonly IReadOnlyDictionary<string, TimeSpan> _tiers;
    private readonly IConsumer<byte[]?, byte[]?> _consumer;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly Dictionary<TopicPartition, KafkaPartitionState> _partitions = [];
    private readonly Dictionary<TopicPartition, Waiting> _paused = [];
    private readonly List<Waiting> _scheduled = [];
    private readonly ConcurrentQueue<Waiting> _failed = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _offsetsChanged;
    private string[] _heldTopics = [];
    private int _disposed;

    public KafkaDelayForwarder(KafkaMessageTransport transport, IReadOnlyDictionary<string, TimeSpan> tiers)
    {
        _transport = transport;
        _tiers = tiers;
        _logger = transport.Logger;
        _consumer = new ConsumerBuilder<byte[]?, byte[]?>(transport.CreateConsumerConfig(transport.Names.DelayGroup))
            .SetLogHandler((_, message) => transport.OnLog(message))
            .SetErrorHandler((_, error) => transport.OnError(error))
            .SetPartitionsAssignedHandler((_, partitions) => OnAssigned(partitions))
            .SetPartitionsRevokedHandler((_, partitions) => OnRevoked(partitions.Select(p => p.TopicPartition).ToList(), commit: true))
            .SetPartitionsLostHandler((_, partitions) => OnRevoked(partitions.Select(p => p.TopicPartition).ToList(), commit: false))
            .Build();
    }

    public void Start() => new Thread(Run) { IsBackground = true, Name = "Foundatio Kafka delay forwarder" }.Start();

    /// <summary>The tier topics whose partitions are paused holding a record that is not due yet. For tests.</summary>
    internal IReadOnlyList<string> HeldTopics => Volatile.Read(ref _heldTopics);

    /// <summary>The tier partitions currently assigned to the consumer. For tests.</summary>
    internal List<TopicPartition> Assignment => _consumer.Assignment;

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
                StoreOffsets();
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

        KafkaPartitionState state;
        lock (_lock)
        {
            if (!_partitions.TryGetValue(partition, out state!))
            {
                state = new KafkaPartitionState(partition);
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
            return;
        }

        lock (_lock)
            state.Read(result.Offset.Value);

        Route(waiting);
    }

    /// <summary>Handles held and scheduled records that are due; returns when the next one is due.</summary>
    private DateTimeOffset? ProcessWaiting(DateTimeOffset now)
    {
        while (_failed.TryDequeue(out var failed))
            _scheduled.Add(failed with { Due = now.Add(RetryDelay) });

        Volatile.Write(ref _heldTopics, _paused.Keys.Select(p => p.Topic).ToArray());
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
                waiting.State.Read(waiting.Result.Offset.Value);

            Route(waiting);
            _consumer.Resume([partition]);
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

        var now = _transport.TimeProvider.GetUtcNow();
        var remaining = (KafkaMessageTransportCodec.GetTime(result, KafkaMessageTransportCodec.DeliverAtHeader) ?? now) - now;
        if (remaining <= TimeSpan.Zero)
        {
            Produce(target, KafkaMessageTransportCodec.WithoutDelay(result), waiting);
        }
        else if (remaining < KafkaDelayTiers.Smallest)
        {
            _scheduled.Add(waiting with { Due = now.Add(remaining) });
        }
        else
        {
            Produce(_transport.Names.Delay(KafkaDelayTiers.Choose(remaining)), KafkaMessageTransportCodec.Copy(result), waiting);
        }
    }

    private void Produce(string topic, Message<byte[]?, byte[]> message, Waiting waiting)
    {
        try
        {
            _transport.Producer.Produce(topic, message, report =>
            {
                if (report.Error.Code is ErrorCode.UnknownTopicOrPart or ErrorCode.Local_UnknownTopic)
                {
                    // The destination was deleted (DeleteAsync) while the message waited.
                    _logger.LogInformation("Dropping delayed record {Offset}: its destination {Topic} no longer exists", waiting.Result.TopicPartitionOffset, topic);
                    Done(waiting);
                    return;
                }

                if (report.Error.IsError)
                {
                    _logger.LogWarning("Could not forward delayed record {Offset} to {Topic}: {Reason}; retrying", waiting.Result.TopicPartitionOffset, topic, report.Error.Reason);
                    _failed.Enqueue(waiting);
                    return;
                }

                Done(waiting);
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not forward delayed record {Offset} to {Topic}; retrying", waiting.Result.TopicPartitionOffset, topic);
            _failed.Enqueue(waiting);
        }
    }

    private void Done(Waiting waiting)
    {
        lock (_lock)
        {
            waiting.State.Pending.Remove(waiting.Result.Offset.Value);
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
                _partitions[partition] = new KafkaPartitionState(partition);
        }
    }

    private void OnRevoked(List<TopicPartition> partitions, bool commit)
    {
        // librdkafka keeps a revoked partition paused, so one assigned back would never be read again.
        var revoked = partitions.ToHashSet();
        var paused = partitions.Where(_paused.Remove).ToList();
        if (paused.Count > 0)
            _consumer.Resume(paused);

        _scheduled.RemoveAll(w => revoked.Contains(w.State.TopicPartition));
        if (commit)
            _transport.Producer.Flush(DrainTimeout);

        List<TopicPartitionOffset> offsets = [];
        lock (_lock)
        {
            foreach (var partition in partitions)
            {
                if (!_partitions.Remove(partition, out var state))
                    continue;

                state.Revoked = true;
                if (state.CommitOffset is { } offset)
                    offsets.Add(new TopicPartitionOffset(partition, offset));
            }
        }

        if (!commit || offsets.Count == 0)
            return;

        try
        {
            foreach (var offset in offsets)
                _consumer.StoreOffset(offset);

            // When closing, Close commits the stored offsets; a commit here could wait behind a JoinGroup.
            if (!_stopping.IsCancellationRequested)
                _consumer.Commit(offsets);
        }
        catch (KafkaException ex)
        {
            _logger.LogWarning(ex, "Could not commit delay offsets while releasing partitions: {Reason}", ex.Error.Reason);
        }
    }

    private sealed record Waiting(ConsumeResult<byte[]?, byte[]?> Result, KafkaPartitionState State, DateTimeOffset Due);
}

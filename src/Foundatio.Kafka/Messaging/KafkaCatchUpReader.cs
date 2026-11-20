using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// Reads a receiver's skipped records again (<see cref="KafkaSkippedRecords"/>) with a consumer of its own, on a
/// dedicated thread that owns every call into it. The consumer has no consumer group: it is assigned partitions
/// directly and never commits, so the receiver's consumer keeps its position and what librdkafka fetched for it. The
/// receiver creates the reader when a group first skips, and disposes it when it stops.
/// </summary>
/// <remarks>
/// <para>
/// For each partition with a skipped record of a group that has drained to half its budget, the reader reads from the
/// lowest such offset, seeking over gaps of more than <see cref="SeekGap"/> offsets, and hands each record to the
/// receiver, which keeps it if it is the first skipped record of its group and the group has room. The last such
/// record that had no room is held back, so that reading it once its group drains needs no seek.
/// </para>
/// <para>
/// A partition with skipped records but none to read yet keeps what librdkafka fetched for it, unless another
/// partition is being read, when it is paused so the reading does not run past its records. A partition without
/// skipped records is unassigned, and the thread waits without polling while there is nothing to read, so the reader
/// fetches nothing while no group skips.
/// </para>
/// </remarks>
internal sealed class KafkaCatchUpReader : IDisposable
{
    /// <summary>How far ahead of the reading the next record to read may be before the reader seeks to it instead.</summary>
    private const long SeekGap = 128;

    private static readonly TimeSpan PollTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    private readonly KafkaMessageTransport _transport;
    private readonly KafkaMessageReceiver _receiver;
    private readonly ILogger _logger;
    private readonly Thread _thread;
    private readonly Dictionary<TopicPartition, Cursor> _cursors = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _wakeLock = new();
    private TaskCompletionSource _wake = NewSignal();
    private int _assignedCount;
    private long _reads;
    private long _seeks;
    private int _disposed;

    public KafkaCatchUpReader(KafkaMessageTransport transport, KafkaMessageReceiver receiver)
    {
        _transport = transport;
        _receiver = receiver;
        _logger = transport.Logger;
        _thread = new Thread(Run) { IsBackground = true, Name = $"Foundatio Kafka catch-up {receiver.Source}" };
    }

    /// <summary>How many partitions the reader's consumer is assigned. For tests.</summary>
    internal int AssignedCount => Volatile.Read(ref _assignedCount);

    /// <summary>How many records the reader has read. For tests.</summary>
    internal long Reads => Interlocked.Read(ref _reads);

    /// <summary>How many times the reader has started reading a partition at an offset. For tests.</summary>
    internal long Seeks => Interlocked.Read(ref _seeks);

    public void Start() => _thread.Start();

    /// <summary>Has the reader look again at what to read: a group with skipped records drained, or partitions moved or finished loading.</summary>
    public void Wake()
    {
        TaskCompletionSource previous;
        lock (_wakeLock)
        {
            previous = _wake;
            _wake = NewSignal();
        }

        previous.TrySetResult();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _stopping.Cancel();
        Wake();
        if (_thread.IsAlive && !_thread.Join(StopTimeout))
            _logger.LogWarning("Timed out closing the catch-up consumer for {Source}", _receiver.Source);
    }

    private void Run()
    {
        IConsumer<byte[]?, byte[]?>? consumer = null;
        try
        {
            consumer = CreateConsumer();
            while (!_stopping.IsCancellationRequested)
            {
                Task wake;
                lock (_wakeLock)
                    wake = _wake.Task;

                if (!Prepare(consumer))
                {
                    wake.Wait();
                    continue;
                }

                while (!_stopping.IsCancellationRequested && !wake.IsCompleted && !Read(consumer))
                {
                }
            }
        }
        catch (Exception ex)
        {
            if (_stopping.IsCancellationRequested)
                _logger.LogDebug(ex, "Catch-up reader for {Source} stopped: {Message}", _receiver.Source, ex.Message);
            else
                _receiver.OnCatchUpFailed(ex);
        }
        finally
        {
            if (consumer is not null)
            {
                try
                {
                    consumer.Close();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error closing the catch-up consumer for {Source}", _receiver.Source);
                }

                consumer.Dispose();
            }

            Volatile.Write(ref _assignedCount, 0);
        }
    }

    private IConsumer<byte[]?, byte[]?> CreateConsumer()
    {
        var config = _transport.CreateConsumerConfig(null);
        // It reads a few records at a time, often after a seek, so a small prefetch wastes little.
        config.QueuedMaxMessagesKbytes = 1024;
        config.MaxPartitionFetchBytes = 256 * 1024;
        return new ConsumerBuilder<byte[]?, byte[]?>(config)
            .SetLogHandler((_, message) => _transport.OnLog(message))
            .SetErrorHandler((_, error) => _transport.OnError(error))
            .Build();
    }

    /// <summary>
    /// Assigns, pauses, resumes and seeks partitions for what the receiver wants read again. Returns whether there is
    /// anything to read.
    /// </summary>
    private bool Prepare(IConsumer<byte[]?, byte[]?> consumer)
    {
        while (true)
        {
            var plan = _receiver.PlanCatchUp();
            if (!HandOverHeld(plan))
                return Apply(consumer, plan);
        }
    }

    /// <summary>Hands the receiver the held-back records it now wants. Returns whether it handed any, which changes the plan.</summary>
    private bool HandOverHeld(Dictionary<TopicPartition, long?> plan)
    {
        bool handed = false;
        foreach (var (partition, target) in plan)
        {
            if (!_cursors.TryGetValue(partition, out var cursor) || cursor.Held is not { } held || held.Offset.Value != target)
                continue;

            var result = _receiver.ReadAgain(held, held.Offset.Value);
            cursor.Held = result == KafkaReadAgainResult.NoRoom ? held : null;
            handed = true;
        }

        return handed;
    }

    private bool Apply(IConsumer<byte[]?, byte[]?> consumer, Dictionary<TopicPartition, long?> plan)
    {
        var gone = _cursors.Keys.Where(p => !plan.ContainsKey(p)).ToList();
        if (gone.Count > 0)
            Unassign(consumer, gone);

        bool reading = plan.Values.Any(t => t is not null);
        foreach (var (partition, target) in plan)
        {
            try
            {
                Apply(consumer, partition, target, reading);
            }
            catch (KafkaException ex)
            {
                _logger.LogDebug(ex, "Could not read {Partition} of {Source} again from {Offset}: {Reason}", partition, _receiver.Source, target, ex.Error.Reason);
                Unassign(consumer, [partition]);
            }
        }

        Volatile.Write(ref _assignedCount, consumer.Assignment.Count);
        return reading;
    }

    private void Apply(IConsumer<byte[]?, byte[]?> consumer, TopicPartition partition, long? target, bool reading)
    {
        if (!_cursors.TryGetValue(partition, out var cursor))
        {
            if (target is { } start)
            {
                consumer.IncrementalAssign([new TopicPartitionOffset(partition, start)]);
                _cursors[partition] = new Cursor { Position = start };
                Interlocked.Increment(ref _seeks);
            }

            return;
        }

        if (target is not { } offset)
        {
            if (reading && !cursor.Paused)
            {
                consumer.Pause([partition]);
                cursor.Paused = true;
            }

            return;
        }

        bool seek = offset < cursor.Position || offset > cursor.Position + SeekGap;
        long from = seek ? offset : cursor.Position;
        if (cursor.Paused)
        {
            // A paused partition dropped what was fetched for it, and librdkafka fetches a resumed one only at its
            // next wake-up; seeking it wakes the fetcher at once.
            consumer.Resume([partition]);
            cursor.Paused = false;
            seek = true;
        }

        if (!seek)
            return;

        consumer.Seek(new TopicPartitionOffset(partition, from));
        cursor.Position = from;
        if (cursor.Held is { } held && held.Offset.Value >= from)
            cursor.Held = null;

        Interlocked.Increment(ref _seeks);
    }

    /// <summary>
    /// Unassigns partitions, resuming paused ones first: librdkafka keeps a partition paused after it is unassigned,
    /// so it would stay paused if it were assigned again.
    /// </summary>
    private void Unassign(IConsumer<byte[]?, byte[]?> consumer, List<TopicPartition> partitions)
    {
        var assigned = partitions.Where(_cursors.ContainsKey).ToList();
        var paused = assigned.Where(p => _cursors[p].Paused).ToList();
        foreach (var partition in assigned)
            _cursors.Remove(partition);

        try
        {
            if (paused.Count > 0)
                consumer.Resume(paused);

            if (assigned.Count > 0)
                consumer.IncrementalUnassign(assigned);
        }
        catch (KafkaException ex)
        {
            _logger.LogDebug(ex, "Could not unassign {Partitions} from the catch-up consumer of {Source}", String.Join(", ", assigned), _receiver.Source);
        }
    }

    /// <summary>
    /// Reads a record and hands it to the receiver. Returns whether what to read may have changed: the record was kept
    /// or held back, or its partition is <see cref="KafkaReadAgainResult.Gone"/>.
    /// </summary>
    private bool Read(IConsumer<byte[]?, byte[]?> consumer)
    {
        ConsumeResult<byte[]?, byte[]?>? result;
        try
        {
            result = consumer.Consume(PollTimeout);
        }
        catch (ConsumeException ex) when (!ex.Error.IsFatal)
        {
            _logger.LogWarning(ex, "Error reading skipped records of {Source} again: {Reason}", _receiver.Source, ex.Error.Reason);
            return true;
        }

        if (result?.Message is null)
            return true;

        if (!_cursors.TryGetValue(result.TopicPartition, out var cursor))
            return false;

        Interlocked.Increment(ref _reads);
        long expected = cursor.Position;
        cursor.Position = result.Offset.Value + 1;
        var outcome = _receiver.ReadAgain(result, expected);
        if (outcome == KafkaReadAgainResult.NoRoom)
            cursor.Held = result;

        return outcome != KafkaReadAgainResult.NotNeeded;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Where the reader is in one partition.</summary>
    private sealed class Cursor
    {
        /// <summary>The offset of the next record the consumer returns for the partition.</summary>
        public long Position { get; set; }

        /// <summary>The last record read whose group had no room for it yet.</summary>
        public ConsumeResult<byte[]?, byte[]?>? Held { get; set; }

        public bool Paused { get; set; }
    }
}

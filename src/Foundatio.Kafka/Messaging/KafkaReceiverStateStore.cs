using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using Foundatio.Extensions;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// Reads the compacted state topic that receivers keep partition state in (<see cref="KafkaReceiverState"/>). One
/// consumer per transport, without a consumer group, reads every partition of the topic from the start on a dedicated
/// thread and keeps the latest value of every key. A read waits until the consumer has caught up with what the topic
/// held when the read was asked for, so it sees every state written before then, by any process.
/// </summary>
internal sealed class KafkaReceiverStateStore : IAsyncDisposable
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan BusyPollTimeout = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan WatermarkTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    private readonly KafkaMessageTransport _transport;
    private readonly string _topic;
    private readonly IReadOnlyList<TopicPartition> _partitions;
    private readonly IConsumer<byte[]?, byte[]?> _consumer;
    private readonly ILogger _logger;
    private readonly Dictionary<string, byte[]> _latest = new(StringComparer.Ordinal);
    private readonly Dictionary<TopicPartition, long> _positions = [];
    private readonly ConcurrentQueue<Read> _requested = new();
    private readonly List<(Read Read, Dictionary<TopicPartition, long> Targets)> _waiting = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    public KafkaReceiverStateStore(KafkaMessageTransport transport, string topic, IReadOnlyList<TopicPartition> partitions)
    {
        _transport = transport;
        _topic = topic;
        _partitions = partitions;
        _logger = transport.Logger;
        var config = transport.CreateConsumerConfig(null);
        config.EnablePartitionEof = true;
        _consumer = new ConsumerBuilder<byte[]?, byte[]?>(config)
            .SetLogHandler((_, message) => transport.OnLog(message))
            .SetErrorHandler((_, error) => transport.OnError(error))
            .Build();
    }

    public void Start() => new Thread(Run) { IsBackground = true, Name = "Foundatio Kafka receiver state" }.Start();

    /// <summary>The latest value written for <paramref name="key"/>, or <c>null</c> when there is none.</summary>
    public Task<byte[]?> LoadAsync(string key, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) == 1 || _stopped.Task.IsCompleted)
            return Task.FromResult<byte[]?>(null);

        var read = new Read(key);
        _requested.Enqueue(read);
        return read.Result.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Writes the value for <paramref name="key"/>; <c>null</c> deletes it.</summary>
    public Task WriteAsync(string key, byte[]? value)
    {
        // The producer's value type cannot say it, but a null value is how Kafka deletes a key from a compacted topic.
        return _transport.Producer.ProduceAsync(_topic, new Message<byte[]?, byte[]> { Key = Encoding.UTF8.GetBytes(key), Value = value! });
    }

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
            _logger.LogWarning("Timed out closing the Kafka receiver state reader");
        }
    }

    private void Run()
    {
        try
        {
            foreach (var partition in _partitions)
                _positions[partition] = _consumer.QueryWatermarkOffsets(partition, WatermarkTimeout).Low.Value;

            _consumer.Assign(_partitions.Select(p => new TopicPartitionOffset(p, Offset.Beginning)));
            while (!_stopping.IsCancellationRequested)
            {
                if (!_requested.IsEmpty)
                {
                    var targets = _partitions.ToDictionary(p => p, p => _consumer.QueryWatermarkOffsets(p, WatermarkTimeout).High.Value);
                    while (_requested.TryDequeue(out var read))
                        _waiting.Add((read, targets));
                }

                CompleteCaughtUp();
                ConsumeResult<byte[]?, byte[]?>? result;
                try
                {
                    result = _consumer.Consume(_waiting.Count > 0 ? BusyPollTimeout : PollTimeout);
                }
                catch (ConsumeException ex) when (!ex.Error.IsFatal)
                {
                    _logger.LogWarning(ex, "Error reading the receiver state topic {Topic}: {Reason}", _topic, ex.Error.Reason);
                    continue;
                }

                if (result is null)
                    continue;

                _positions[result.TopicPartition] = result.IsPartitionEOF ? result.Offset.Value : result.Offset.Value + 1;
                if (result.IsPartitionEOF || result.Message?.Key is not { } key)
                    continue;

                string name = Encoding.UTF8.GetString(key);
                if (result.Message.Value is { Length: > 0 } value)
                    _latest[name] = value;
                else
                    _latest.Remove(name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kafka receiver state reader stopped: {Message}", ex.Message);
        }
        finally
        {
            foreach (var (read, _) in _waiting)
                read.Result.TrySetResult(null);

            while (_requested.TryDequeue(out var read))
                read.Result.TrySetResult(null);

            try
            {
                _consumer.Close();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error closing the Kafka receiver state reader");
            }

            _consumer.Dispose();
            _stopped.TrySetResult();
        }
    }

    private void CompleteCaughtUp()
    {
        for (int i = _waiting.Count - 1; i >= 0; i--)
        {
            var (read, targets) = _waiting[i];
            if (!targets.All(t => _positions.GetValueOrDefault(t.Key) >= t.Value))
                continue;

            _waiting.RemoveAt(i);
            read.Result.TrySetResult(_latest.GetValueOrDefault(read.Key));
        }
    }

    private sealed record Read(string Key)
    {
        public TaskCompletionSource<byte[]?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

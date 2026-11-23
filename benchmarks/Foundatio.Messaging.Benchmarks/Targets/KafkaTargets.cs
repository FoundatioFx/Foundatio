using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using Confluent.Kafka.Admin;

namespace Foundatio.Messaging.Benchmarks.Targets;

public static class KafkaTargets
{
    public static IBenchTarget Legacy(BenchOptions options) => new LegacyBusTarget(options, () => new KafkaMessageBus(o =>
    {
        o.BootstrapServers(options.Kafka)
            .Topic("legacy-" + options.Name)
            .GroupId("bench")
            .TopicReplicationFactor(1)
            .TopicNumberOfPartitions(4)
            .AllowAutoCreateTopics(true)
            .AutoOffsetReset(AutoOffsetReset.Earliest);
        if (options.KafkaConsumerProtocol)
            o.GroupProtocol(GroupProtocol.Consumer);
        return o;
    }), durable: true);
}

/// <summary>
/// Confluent.Kafka by hand: <c>ProduceAsync</c> (batches in flight together, acks=all), one consumer assigned to each
/// partition of the topic, each feeding the handlers from a receive loop of its own, offsets stored after handling and
/// auto-committed every second. The floor for one consumer group, without the time a group takes to share partitions.
/// </summary>
public sealed class RawKafkaTarget(BenchOptions options) : BenchTargetBase(options)
{
    private const int Partitions = 4;

    private readonly string _topic = "raw-" + options.Name;
    private readonly List<IConsumer<string?, byte[]>> _consumers = [];
    private IProducer<string?, byte[]>? _producer;
    private RawPump<(IConsumer<string?, byte[]> Consumer, ConsumeResult<string?, byte[]> Result)>? _pump;

    private IProducer<string?, byte[]> Producer => _producer ??= new ProducerBuilder<string?, byte[]>(new ProducerConfig
    {
        BootstrapServers = Options.Kafka,
        Acks = Acks.All,
        EnableIdempotence = true,
        LingerMs = Options.KafkaLingerMs ?? 0
    }).Build();

    public override async Task InitAsync(CancellationToken cancellationToken)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = Options.Kafka }).Build();
        try
        {
            await admin.CreateTopicsAsync([new TopicSpecification { Name = _topic, NumPartitions = Partitions, ReplicationFactor = 1 }]);
        }
        catch (CreateTopicsException ex) when (ex.Results.All(r => r.Error.Code == ErrorCode.TopicAlreadyExists)) { }
    }

    public override Task SendAsync(BenchMessage message, CancellationToken cancellationToken) =>
        Producer.ProduceAsync(_topic, new Message<string?, byte[]> { Key = message.Group, Value = JsonSerializer.SerializeToUtf8Bytes(message) }, cancellationToken);

    public override Task SendBatchAsync(IReadOnlyList<BenchMessage> messages, CancellationToken cancellationToken) =>
        Task.WhenAll(messages.Select(m => Producer.ProduceAsync(_topic, new Message<string?, byte[]> { Key = m.Group, Value = JsonSerializer.SerializeToUtf8Bytes(m) }, cancellationToken)));

    public override Task StartAsync(int concurrency, Func<BenchMessage, ValueTask> handler, CancellationToken cancellationToken)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = Options.Kafka,
            GroupId = "raw-bench",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true,
            EnableAutoOffsetStore = false,
            AutoCommitIntervalMs = 1000
        };

        var idle = new ConcurrentQueue<IConsumer<string?, byte[]>>();
        for (int i = 0; i < Partitions; i++)
        {
            var consumer = new ConsumerBuilder<string?, byte[]>(config).Build();
            consumer.Assign(new TopicPartitionOffset(_topic, i, Offset.Stored));
            _consumers.Add(consumer);
            idle.Enqueue(consumer);
        }

        // Each receive loop takes a consumer nobody is reading; with one loop per consumer, each keeps its own.
        _pump = new RawPump<(IConsumer<string?, byte[]> Consumer, ConsumeResult<string?, byte[]> Result)>(
            (max, token) => Task.Run<IReadOnlyList<(IConsumer<string?, byte[]>, ConsumeResult<string?, byte[]>)>>(() =>
            {
                var results = new List<(IConsumer<string?, byte[]>, ConsumeResult<string?, byte[]>)>(max);
                if (!idle.TryDequeue(out var consumer))
                    return results;

                try
                {
                    var first = consumer.Consume(TimeSpan.FromMilliseconds(100));
                    if (first is null)
                        return results;
                    results.Add((consumer, first));
                    while (results.Count < max && consumer.Consume(TimeSpan.Zero) is { } next)
                        results.Add((consumer, next));
                    return results;
                }
                finally
                {
                    idle.Enqueue(consumer);
                }
            }, token),
            item => JsonSerializer.Deserialize<BenchMessage>(item.Result.Message.Value)!,
            items =>
            {
                // out-of-order stores can move a partition's offset past an unfinished record; the floor accepts that
                foreach (var (consumer, result) in items)
                    consumer.StoreOffset(result);
                return Task.CompletedTask;
            },
            maxBatch: 500,
            receivers: Partitions);
        _pump.Start(concurrency, handler);
        return Task.CompletedTask;
    }

    public override async Task StopAsync()
    {
        if (_pump is not null)
            await _pump.DisposeAsync();
        _pump = null;
        foreach (var consumer in _consumers)
        {
            consumer.Close();
            consumer.Dispose();
        }

        _consumers.Clear();
    }

    public override async Task CleanupAsync()
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = Options.Kafka }).Build();
        try
        {
            await admin.DeleteTopicsAsync([_topic]);
        }
        catch (DeleteTopicsException) { }
    }

    public override async ValueTask DisposeAsync()
    {
        await StopAsync();
        _producer?.Flush(TimeSpan.FromSeconds(5));
        _producer?.Dispose();
        await base.DisposeAsync();
    }
}

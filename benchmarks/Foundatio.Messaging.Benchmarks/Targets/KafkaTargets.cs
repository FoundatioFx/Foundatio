using System;
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
/// Confluent.Kafka by hand: <c>ProduceAsync</c> (batches in flight together, acks=all), one consumer feeding the
/// handlers, offsets stored after handling and auto-committed every second. The floor for one consumer group.
/// </summary>
public sealed class RawKafkaTarget(BenchOptions options) : BenchTargetBase(options)
{
    private readonly string _topic = "raw-" + options.Name;
    private IProducer<string?, byte[]>? _producer;
    private IConsumer<string?, byte[]>? _consumer;
    private RawPump<ConsumeResult<string?, byte[]>>? _pump;

    private IProducer<string?, byte[]> Producer => _producer ??= new ProducerBuilder<string?, byte[]>(new ProducerConfig
    {
        BootstrapServers = Options.Kafka,
        Acks = Acks.All,
        EnableIdempotence = true,
        LingerMs = 5
    }).Build();

    public override async Task InitAsync(CancellationToken cancellationToken)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = Options.Kafka }).Build();
        try
        {
            await admin.CreateTopicsAsync([new TopicSpecification { Name = _topic, NumPartitions = 4, ReplicationFactor = 1 }]);
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
        if (Options.KafkaConsumerProtocol)
            config.GroupProtocol = GroupProtocol.Consumer;

        var consumer = _consumer = new ConsumerBuilder<string?, byte[]>(config).Build();
        consumer.Subscribe(_topic);
        _pump = new RawPump<ConsumeResult<string?, byte[]>>(
            (max, token) => Task.Run<IReadOnlyList<ConsumeResult<string?, byte[]>>>(() =>
            {
                var results = new List<ConsumeResult<string?, byte[]>>(max);
                var first = consumer.Consume(TimeSpan.FromMilliseconds(100));
                if (first is null)
                    return results;
                results.Add(first);
                while (results.Count < max && consumer.Consume(TimeSpan.Zero) is { } next)
                    results.Add(next);
                return results;
            }, token),
            result => JsonSerializer.Deserialize<BenchMessage>(result.Message.Value)!,
            results =>
            {
                // out-of-order stores can move a partition's offset past an unfinished record; the floor accepts that
                foreach (var result in results)
                    consumer.StoreOffset(result);
                return Task.CompletedTask;
            },
            maxBatch: 500,
            receivers: 1);
        _pump.Start(concurrency, handler);
        return Task.CompletedTask;
    }

    public override async Task StopAsync()
    {
        if (_pump is not null)
            await _pump.DisposeAsync();
        _pump = null;
        _consumer?.Close();
        _consumer?.Dispose();
        _consumer = null;
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

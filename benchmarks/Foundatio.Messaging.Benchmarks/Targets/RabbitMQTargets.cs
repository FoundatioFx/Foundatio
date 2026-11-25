using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Foundatio.Messaging.Benchmarks.Targets;

public static class RabbitMQTargets
{
    /// <summary>The legacy bus with a durable quorum subscription queue, so a backlog can be prefilled.</summary>
    public static IBenchTarget Legacy(BenchOptions options) => new LegacyBusTarget(options, () => new RabbitMQMessageBus(o => o
        .ConnectionString(options.RabbitConnection!)
        .Topic(options.Name)
        .SubscriptionQueueName(options.Name + ".legacy")
        .UseQuorumQueues()
        .IsDurable(true)
        .PublisherConfirmsEnabled(true)
        .PrefetchCount((ushort)Math.Max(2 * options.Concurrency, 10))), durable: true);
}

/// <summary>
/// RabbitMQ.Client by hand: a quorum queue, publisher confirms (pipelined, the channel default rate limiter), a push consumer
/// with prefetch twice the handler count, one ack per message. The floor for a quorum queue with confirms.
/// </summary>
public sealed class RawRabbitMQTarget(BenchOptions options) : BenchTargetBase(options)
{
    private readonly string _queue = "raw-" + options.Name;
    // the topic pattern publishes to a fanout exchange bound to the queue, as a durable subscription does
    private string Exchange => Options.Pattern == Pattern.Topic ? "raw-x-" + Options.Name : "";
    private string RoutingKey => Options.Pattern == Pattern.Topic ? "" : _queue;
    private IConnection? _connection;
    private IChannel? _publisher;
    private IChannel? _consumer;
    private readonly SemaphoreSlim _connect = new(1, 1);

    private async Task<IConnection> ConnectAsync()
    {
        if (_connection is not null)
            return _connection;

        await _connect.WaitAsync();
        try
        {
            return _connection ??= await new ConnectionFactory { Uri = new Uri(Options.RabbitConnection!), ConsumerDispatchConcurrency = (ushort)Math.Max(1, Options.Concurrency) }.CreateConnectionAsync();
        }
        finally
        {
            _connect.Release();
        }
    }

    public override async Task InitAsync(CancellationToken cancellationToken)
    {
        var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(_queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" }, cancellationToken: cancellationToken);
        if (Options.Pattern == Pattern.Topic)
        {
            await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Fanout, durable: true, cancellationToken: cancellationToken);
            await channel.QueueBindAsync(_queue, Exchange, "", cancellationToken: cancellationToken);
        }
    }

    private async Task<IChannel> PublisherAsync()
    {
        if (_publisher is not null)
            return _publisher;

        var connection = await ConnectAsync();
        return _publisher ??= await connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true));
    }

    public override async Task SendAsync(BenchMessage message, CancellationToken cancellationToken)
    {
        var channel = await PublisherAsync();
        await channel.BasicPublishAsync(Exchange, RoutingKey, mandatory: true, new BasicProperties { Persistent = true, ContentType = "application/json" }, JsonSerializer.SerializeToUtf8Bytes(message), cancellationToken);
    }

    public override async Task SendBatchAsync(IReadOnlyList<BenchMessage> messages, CancellationToken cancellationToken)
    {
        var channel = await PublisherAsync();
        var properties = new BasicProperties { Persistent = true, ContentType = "application/json" };
        await Task.WhenAll(messages.Select(m => channel.BasicPublishAsync(Exchange, RoutingKey, mandatory: true, properties, JsonSerializer.SerializeToUtf8Bytes(m), cancellationToken).AsTask()));
    }

    public override async Task StartAsync(int concurrency, Func<BenchMessage, ValueTask> handler, CancellationToken cancellationToken)
    {
        var connection = await ConnectAsync();
        _consumer = await connection.CreateChannelAsync(new CreateChannelOptions(false, false, consumerDispatchConcurrency: (ushort)concurrency), cancellationToken);
        await _consumer.BasicQosAsync(0, Environment.GetEnvironmentVariable("BENCH_PREFETCH") is { } prefetch ? UInt16.Parse(prefetch) : (ushort)Math.Max(2 * concurrency, 10), false, cancellationToken);
        var consumer = new AsyncEventingBasicConsumer(_consumer);
        var channel = _consumer;
        consumer.ReceivedAsync += async (_, args) =>
        {
            var message = JsonSerializer.Deserialize<BenchMessage>(args.Body.Span)!;
            await handler(message);
            await channel.BasicAckAsync(args.DeliveryTag, multiple: false);
        };
        await _consumer.BasicConsumeAsync(_queue, autoAck: false, consumer, cancellationToken);
    }

    public override async Task StopAsync()
    {
        if (_consumer is not null)
            await _consumer.CloseAsync();
        _consumer = null;
    }

    public override async Task CleanupAsync()
    {
        var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.QueueDeleteAsync(_queue);
        if (Options.Pattern == Pattern.Topic)
            await channel.ExchangeDeleteAsync(Exchange);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_publisher is not null)
            await _publisher.DisposeAsync();
        if (_connection is not null)
            await _connection.DisposeAsync();
        _connect.Dispose();
        await base.DisposeAsync();
    }
}

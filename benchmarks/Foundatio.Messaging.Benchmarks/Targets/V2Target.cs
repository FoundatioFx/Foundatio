using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using Foundatio.Messaging.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging.Benchmarks.Targets;

/// <summary>v2: <see cref="IMessageClient"/> to send, a consumer or service subscriber to handle, each in its own host.</summary>
public sealed class V2Target(BenchOptions options, BrokerCalls calls) : BenchTargetBase(options)
{
    private IHost? _producer;
    private IHost? _consumer;
    private IMessageClient? _client;
    private Func<BenchMessage, ValueTask>? _handler;
    private InMemoryMessageTransport? _inMemory;

    public override async Task InitAsync(CancellationToken cancellationToken)
    {
        // a receiver creates its queue or subscription, so start one and stop it again
        _handler = _ => ValueTask.CompletedTask;
        using var host = BuildHost(Options.Concurrency, handle: true);
        await host.StartAsync(cancellationToken);
        await WaitForReceiverAsync(cancellationToken);
        await host.StopAsync(cancellationToken);
    }

    private async Task WaitForReceiverAsync(CancellationToken cancellationToken)
    {
        // Kafka joins its group in the background of the hosted service; give it time before sending
        if (Options.Transport == "kafka")
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        else
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
    }

    private IMessageClient Client
    {
        get
        {
            if (_client is not null)
                return _client;

            _producer = BuildHost(Options.Concurrency, handle: false);
            _producer.StartAsync().GetAwaiter().GetResult();
            return _client = _producer.Services.GetRequiredService<IMessageClient>();
        }
    }

    public override Task SendAsync(BenchMessage message, CancellationToken cancellationToken)
    {
        if (Options.Pattern == Pattern.Queue)
            return Client.SendAsync(message, SendOptionsFor(message), cancellationToken);

        return Client.PublishAsync(message, PublishOptionsFor(message), cancellationToken);
    }

    public override Task SendBatchAsync(IReadOnlyList<BenchMessage> messages, CancellationToken cancellationToken)
    {
        if (Options.Groups > 0)
        {
            // GroupKey is per call, so a grouped batch is one call per group
            return Task.WhenAll(messages.GroupBy(m => m.Group).Select(g => Options.Pattern == Pattern.Queue
                ? Client.SendBatchAsync(g.ToList(), SendOptionsFor(g.First()), cancellationToken)
                : Client.PublishBatchAsync(g.ToList(), PublishOptionsFor(g.First()), cancellationToken)));
        }

        if (Options.Pattern == Pattern.Queue)
            return Client.SendBatchAsync(messages, SendOptionsFor(null), cancellationToken);

        return Client.PublishBatchAsync(messages, PublishOptionsFor(null), cancellationToken);
    }

    private SendOptions? SendOptionsFor(BenchMessage? message) =>
        message?.Group is null && Options.Delay <= TimeSpan.Zero ? null : new SendOptions { GroupKey = message?.Group, Delay = Options.Delay > TimeSpan.Zero ? Options.Delay : null };

    private PublishOptions? PublishOptionsFor(BenchMessage? message) =>
        message?.Group is null && Options.Delay <= TimeSpan.Zero ? null : new PublishOptions { GroupKey = message?.Group, Delay = Options.Delay > TimeSpan.Zero ? Options.Delay : null };

    public override async Task StartAsync(int concurrency, Func<BenchMessage, ValueTask> handler, CancellationToken cancellationToken)
    {
        _handler = handler;
        _consumer = BuildHost(concurrency, handle: true);
        await _consumer.StartAsync(cancellationToken);
    }

    public override async Task StopAsync()
    {
        if (_consumer is not null)
        {
            await _consumer.StopAsync();
            _consumer.Dispose();
            _consumer = null;
        }

        if (_producer is not null)
        {
            await _producer.StopAsync();
            _producer.Dispose();
            _producer = null;
            _client = null;
        }
    }

    public override async Task CleanupAsync()
    {
        using var host = BuildHost(Options.Concurrency, handle: false);
        var transport = host.Services.GetRequiredService<IMessageTransport>();
        var source = Options.Pattern == Pattern.Queue ? MessageSource.Queue(QueueName) : MessageSource.Subscription(QueueName, "bench");
        try
        {
            await transport.DeleteAsync(source);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"delete {source} failed: {ex.Message}");
        }
    }

    private string QueueName => Options.Name;

    private IHost BuildHost(int concurrency, bool handle)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Logging.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddFoundatioMessaging(m =>
        {
            Configure(m, calls);
            m.UseServiceName("bench");
            m.MapMessage<BenchMessage>("bench.message", queue: QueueName, topic: QueueName);
            if (!handle)
                return;

            if (Options.Pattern == Pattern.Queue)
                m.AddConsumer<BenchMessage>((message, _, _) => _handler!(message).AsTask(), new HandlerOptions { MaxConcurrency = concurrency });
            else
                m.AddSubscriber<BenchMessage>((message, _, _) => _handler!(message).AsTask(), new SubscriptionOptions { MaxConcurrency = concurrency, Name = "bench" });
        });

        return builder.Build();
    }

    private void Configure(FoundatioMessagingBuilder builder, BrokerCalls brokerCalls)
    {
        switch (Options.Transport)
        {
            case "inmemory":
                builder.UseTransport(_inMemory ??= new InMemoryMessageTransport());
                break;
            case "redis":
                builder.UseRedis(((RedisCalls)brokerCalls).Connection, new RedisMessageTransportOptions { Prefix = "bench" });
                break;
            case "sqs":
                builder.UseSqs(new SqsMessageTransportOptions { ConnectionString = Options.Sqs, Fifo = Options.Fifo });
                break;
            case "kafka":
                builder.UseKafka(new KafkaMessageTransportOptions
                {
                    BootstrapServers = Options.Kafka,
                    Prefix = "bench-" + Options.Name,
                    ReplicationFactor = 1,
                    ConfigureConsumer = Options.KafkaConsumerProtocol ? c => c.GroupProtocol = GroupProtocol.Consumer : null
                });
                break;
            case "servicebus":
                builder.UseAzureServiceBus(new AzureServiceBusMessageTransportOptions
                {
                    ConnectionString = Options.ServiceBus,
                    RequiresSession = Options.Groups > 0,
                    MaxDeliveryCount = 10
                });
                break;
            case "rabbitmq":
                builder.UseRabbitMQ(new RabbitMQMessageTransportOptions { ConnectionString = Options.RabbitConnection, Prefix = "bench" });
                break;
            default:
                throw new ArgumentException($"Unknown transport {Options.Transport}");
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await StopAsync();
        await base.DisposeAsync();
    }
}

using Foundatio.Messaging;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Foundatio.Messaging.Benchmarks;

public sealed class FoundatioDriver(BenchmarkOptions options, string prefix) : IMessagingDriver
{
    private readonly List<IMessageSubscription> _subscriptions = [];
    private readonly ILoggerFactory _logs = LoggerFactory.Create(b => b.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning));
    private IMessageTransport? _transport;
    private MessageBus? _bus;
    private IConnectionMultiplexer? _redis;
    private Exception? _fault;

    public async Task StartAsync(Action<int, LoadMessage> received, CancellationToken token)
    {
        if (options.Transport == "redis")
        {
            _redis = await ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable("PERF_REDIS") ?? "localhost:16379");
            _transport = new RedisStreamsMessageTransport(new() { ConnectionMultiplexer = _redis, KeyPrefix = prefix + ":" });
        }
        else if (options.Transport == "sqs")
            _transport = new AwsMessageTransport(new AwsMessageTransportOptions { ResourcePrefix = prefix, ServiceUrl = AwsResources.ServiceUrl, Region = AwsResources.Region, Credentials = AwsResources.LocalCredentials });
        else _transport = new InMemoryMessageTransport();
        _bus = new MessageBus(_transport, new()
        {
            OwnsTransport = false,
            LoggerFactory = _logs,
            MessageTypes = new MessageTypeRegistry([new("load.v1", typeof(LoadMessage))])
        });
        for (int group = 0; group < options.DeliveryCopies; group++)
        {
            int subscriber = group;
            async Task HandleAsync(IMessageContext<LoadMessage> context, CancellationToken ct)
            {
                try { await context.CompleteAsync(ct); received(subscriber, context.Message); }
                catch (Exception ex) { Interlocked.CompareExchange(ref _fault, ex, null); throw; }
            }
            var subscription = options.Scenario == "queue"
                ? await _bus.ConsumeAsync<LoadMessage>(HandleAsync, new() { Destination = "input", AckMode = AckMode.Manual, MaxConcurrency = options.ConsumerConcurrency }, token)
                : await _bus.SubscribeAsync<LoadMessage>(HandleAsync, new() { Topic = "events", Subscription = "group" + group, AckMode = AckMode.Manual, MaxConcurrency = options.ConsumerConcurrency }, token);
            _subscriptions.Add(subscription);
            await subscription.WaitUntilReadyAsync(token);
        }
    }

    public async Task SendAsync(LoadMessage[] messages, CancellationToken token)
    {
        if (options.Scenario == "queue")
        {
            if (messages.Length == 1) await _bus!.SendAsync(messages[0], new() { Destination = "input" }, token);
            else await _bus!.SendBatchAsync(messages, new() { Destination = "input" }, token);
        }
        else
        {
            if (messages.Length == 1) await _bus!.PublishAsync(messages[0], new() { Topic = "events" }, token);
            else await _bus!.PublishBatchAsync(messages, new() { Topic = "events" }, token);
        }
    }

    public void ThrowIfFaulted()
    {
        if (Volatile.Read(ref _fault) is { } fault) throw new InvalidOperationException("Foundatio receive or acknowledgement failed.", fault);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var subscription in _subscriptions) await subscription.DisposeAsync();
        if (_bus is not null) await _bus.DisposeAsync();
        if (_transport is not null) await _transport.DisposeAsync();
        if (_redis is not null)
        {
            foreach (var endpoint in _redis.GetEndPoints())
            {
                var server = _redis.GetServer(endpoint);
                if (server.IsReplica) continue;
                var keys = server.Keys(pattern: prefix + ":*").ToArray();
                if (keys.Length > 0) await _redis.GetDatabase().KeyDeleteAsync(keys);
            }
            await _redis.DisposeAsync();
        }
        if (options.Transport == "sqs") await AwsResources.CleanupAsync(prefix);
        _logs.Dispose();
    }
}

using MassTransit;

namespace Foundatio.Messaging.Benchmarks;

public sealed class MassTransitDriver(BenchmarkOptions options, string prefix) : IMessagingDriver, IReceiveObserver
{
    private IBusControl? _bus;
    private ISendEndpoint? _send;
    private ConnectHandle? _observer;
    private Action<int, LoadMessage> _received = null!;
    private Exception? _fault;

    public async Task StartAsync(Action<int, LoadMessage> received, CancellationToken token)
    {
        _received = received;
        if (options.Transport == "sqs")
        {
            _bus = Bus.Factory.CreateUsingAmazonSqs(cfg =>
            {
                cfg.Host(AwsResources.Region.SystemName, h =>
                {
                    h.Scope(prefix, true);
                    if (AwsResources.LocalCredentials is { } credentials) h.Credentials(credentials);
                    h.Config(AwsResources.SqsConfig); h.Config(AwsResources.SnsConfig);
                });
                cfg.Message<LoadMessage>(m => m.SetEntityName(prefix + "events"));
                for (int group = 0; group < options.DeliveryCopies; group++)
                {
                    int subscriber = group;
                    cfg.ReceiveEndpoint("input" + group, e => Configure(e, subscriber));
                }
            });
        }
        else
        {
            _bus = Bus.Factory.CreateUsingInMemory(cfg =>
            {
                for (int group = 0; group < options.DeliveryCopies; group++)
                {
                    int subscriber = group;
                    cfg.ReceiveEndpoint("input" + group, e => Configure(e, subscriber));
                }
            });
        }
        _observer = _bus.ConnectReceiveObserver(this);
        await _bus.StartAsync(token);
        _send = await _bus.GetSendEndpoint(new Uri("queue:input0"));
    }

    private void Configure(IReceiveEndpointConfigurator endpoint, int subscriber)
    {
        endpoint.PrefetchCount = options.Prefetch;
        endpoint.ConcurrentMessageLimit = options.ConsumerConcurrency;
        endpoint.ConfigureConsumeTopology = options.Scenario == "pubsub";
        endpoint.Handler<LoadMessage>(context =>
        {
            context.ReceiveContext.GetOrAddPayload(() => new Receipt(subscriber, context.Message));
            return Task.CompletedTask;
        });
    }

    public Task SendAsync(LoadMessage[] messages, CancellationToken token)
    {
        if (options.Scenario == "queue") return messages.Length == 1 ? _send!.Send(messages[0], token) : _send!.SendBatch(messages, token);
        return messages.Length == 1 ? _bus!.Publish(messages[0], token) : _bus!.PublishBatch(messages, token);
    }

    public Task PreReceive(ReceiveContext context) => Task.CompletedTask;
    public Task PostReceive(ReceiveContext context)
    {
        if (context.TryGetPayload<Receipt>(out var receipt)) _received(receipt.Subscriber, receipt.Message);
        return Task.CompletedTask;
    }
    public Task PostConsume<T>(ConsumeContext<T> context, TimeSpan duration, string consumerType) where T : class => Task.CompletedTask;
    public Task ConsumeFault<T>(ConsumeContext<T> context, TimeSpan duration, string consumerType, Exception exception) where T : class => ReceiveFault(context.ReceiveContext, exception);
    public Task ReceiveFault(ReceiveContext context, Exception exception) { Interlocked.CompareExchange(ref _fault, exception, null); return Task.CompletedTask; }
    public void ThrowIfFaulted()
    {
        if (Volatile.Read(ref _fault) is { } fault) throw new InvalidOperationException("MassTransit receive or acknowledgement failed.", fault);
    }
    public async ValueTask DisposeAsync()
    {
        if (_bus is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _bus.StopAsync(timeout.Token);
            _observer?.Disconnect();
        }
        if (options.Transport == "sqs") await AwsResources.CleanupAsync(prefix);
    }
    private sealed record Receipt(int Subscriber, LoadMessage Message);
}

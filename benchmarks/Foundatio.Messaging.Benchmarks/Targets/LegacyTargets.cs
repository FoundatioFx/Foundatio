using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Queues;

namespace Foundatio.Messaging.Benchmarks.Targets;

/// <summary>The legacy <see cref="IQueue{T}"/>: <c>EnqueueAsync</c>, and one <c>StartWorkingAsync</c> worker per handler.</summary>
public sealed class LegacyQueueTarget(BenchOptions options, Func<IQueue<BenchMessage>> factory) : BenchTargetBase(options)
{
    private IQueue<BenchMessage>? _producer;
    private IQueue<BenchMessage>? _consumer;
    private CancellationTokenSource? _stop;

    private IQueue<BenchMessage> Producer => _producer ??= factory();

    public override async Task InitAsync(CancellationToken cancellationToken)
    {
        await Producer.GetQueueStatsAsync();
    }

    public override Task SendAsync(BenchMessage message, CancellationToken cancellationToken) =>
        Producer.EnqueueAsync(message, Options.Delay > TimeSpan.Zero ? new QueueEntryOptions { DeliveryDelay = Options.Delay } : null);

    public override async Task StartAsync(int concurrency, Func<BenchMessage, ValueTask> handler, CancellationToken cancellationToken)
    {
        _consumer = factory();
        _stop = new CancellationTokenSource();
        for (int i = 0; i < concurrency; i++)
            await _consumer.StartWorkingAsync(async (entry, _) => await handler(entry.Value), autoComplete: true, _stop.Token);
    }

    public override Task StopAsync()
    {
        _stop?.Cancel();
        _consumer?.Dispose();
        _consumer = null;
        return Task.CompletedTask;
    }

    public override async Task CleanupAsync()
    {
        try
        {
            await Producer.DeleteQueueAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"delete queue failed: {ex.Message}");
        }

        _producer?.Dispose();
    }
}

/// <summary>The legacy <see cref="IMessageBus"/>: <c>PublishAsync</c> and <c>SubscribeAsync</c>.</summary>
public sealed class LegacyBusTarget(BenchOptions options, Func<IMessageBus> factory, bool durable) : BenchTargetBase(options)
{
    private IMessageBus? _producer;
    private IMessageBus? _consumer;

    private IMessageBus Producer => _producer ??= factory();

    public override async Task InitAsync(CancellationToken cancellationToken)
    {
        if (!durable)
            return;

        // a durable subscription exists once something subscribed
        using var bus = factory();
        await bus.SubscribeAsync<BenchMessage>(_ => Task.CompletedTask, cancellationToken);
    }

    public override Task SendAsync(BenchMessage message, CancellationToken cancellationToken) =>
        Producer.PublishAsync(message, Options.Delay > TimeSpan.Zero ? new MessageOptions { DeliveryDelay = Options.Delay } : null, cancellationToken);

    public override async Task StartAsync(int concurrency, Func<BenchMessage, ValueTask> handler, CancellationToken cancellationToken)
    {
        _consumer = factory();
        await _consumer.SubscribeAsync<BenchMessage>(async (message, _) => await handler(message), cancellationToken);
    }

    public override async Task StopAsync()
    {
        if (_consumer is not null)
            await _consumer.DisposeAsync();
        _consumer = null;
    }

    public override async ValueTask DisposeAsync()
    {
        await StopAsync();
        if (_producer is not null)
            await _producer.DisposeAsync();
        await base.DisposeAsync();
    }
}

using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace Foundatio.Benchmarks.Messaging;

/// <summary>
/// Publishing to one or five subscriptions, 100-byte messages, each subscription handled at concurrency 8. Each
/// operation is one published message, handled once by every subscription. The legacy bus row publishes from 8 tasks
/// to as many subscribers.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("Messaging")]
public class MessagingFanOutBenchmarks
{
    private const int Batch = 5000;
    private const int Concurrency = 8;

    private readonly Countdown _countdown = new();
    private BenchPayload _payload;
    private BenchPayload[] _batch;
    private InMemoryMessageTransport _transport;
    private BenchHost _host;
    private IMessageClient _client;
    private InMemoryMessageBus _legacyBus;

    [Params(1, 5)]
    public int Subscriptions { get; set; }

    [GlobalSetup(Target = nameof(V2Publish))]
    public async Task SetupV2Async()
    {
        _payload = BenchPayload.Create(100);
        _batch = Enumerable.Repeat(_payload, Batch).ToArray();
        _transport = new InMemoryMessageTransport();
        _host = await BenchHost.StartAsync(s => s.AddFoundatioMessaging(m =>
        {
            m.UseTransport(_transport).MapMessage<BenchPayload>("bench.payload");
            for (int i = 0; i < Subscriptions; i++)
            {
                m.AddSubscriber<BenchPayload>((_, _, _) =>
                {
                    _countdown.Signal();
                    return Task.CompletedTask;
                }, new SubscriptionOptions { Name = $"bench-{i}", MaxConcurrency = Concurrency });
            }
        }));
        _client = _host.Services.GetRequiredService<IMessageClient>();
    }

    [GlobalSetup(Target = nameof(LegacyBusPublish))]
    public async Task SetupLegacyAsync()
    {
        _payload = BenchPayload.Create(100);
        _legacyBus = new InMemoryMessageBus();
        for (int i = 0; i < Subscriptions; i++)
            await _legacyBus.SubscribeAsync<BenchPayload>(_ => _countdown.Signal());
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_host is not null)
            await _host.DisposeAsync();
        if (_transport is not null)
            await _transport.DisposeAsync();
        _legacyBus?.Dispose();
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Batch)]
    public async Task V2Publish()
    {
        var done = _countdown.Reset(Batch * Subscriptions);
        await _client.PublishBatchAsync(_batch);
        await done.WaitOrThrowAsync(nameof(V2Publish));
    }

    [Benchmark(OperationsPerInvoke = Batch)]
    public async Task LegacyBusPublish()
    {
        var done = _countdown.Reset(Batch * Subscriptions);
        await Task.WhenAll(Enumerable.Range(0, Concurrency).Select(_ => Task.Run(async () =>
        {
            for (int i = 0; i < Batch / Concurrency; i++)
                await _legacyBus.PublishAsync(_payload);
        })));

        await done.WaitOrThrowAsync(nameof(LegacyBusPublish));
    }
}

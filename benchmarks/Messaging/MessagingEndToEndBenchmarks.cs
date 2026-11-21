using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Queues;
using Microsoft.Extensions.DependencyInjection;

namespace Foundatio.Benchmarks.Messaging;

/// <summary>
/// Send to handled, in process. <c>Latency</c> sends one message and waits for its handler; <c>Throughput</c> sends
/// 10,000 at once and waits for all of them, so each operation is one message. Compared at the same handler
/// concurrency: the v2 consumer on the in-memory transport, the legacy <see cref="InMemoryQueue{T}"/> worked by
/// <see cref="QueueJobBase{T}"/> loops, the legacy <see cref="InMemoryMessageBus"/> (each publish waits for its
/// subscriber, so concurrency is the number of publishers), and the floor: a channel of JSON bodies read by as many loops.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[BenchmarkCategory("Messaging")]
public class MessagingEndToEndBenchmarks
{
    private const int Batch = 10_000;

    private readonly Countdown _countdown = new();
    private BenchPayload _payload;
    private BenchPayload[] _batch;

    private InMemoryMessageTransport _transport;
    private BenchHost _host;
    private IMessageClient _client;
    private InMemoryQueue<BenchPayload> _legacyQueue;
    private LegacyQueueWorkers _legacyWorkers;
    private InMemoryMessageBus _legacyBus;
    private ChannelFloor _floor;

    [Params(1, 8, 64)]
    public int Concurrency { get; set; }

    [Params(100, 16384)]
    public int PayloadBytes { get; set; }

    private void CreatePayloads()
    {
        _payload = BenchPayload.Create(PayloadBytes);
        _batch = Enumerable.Repeat(_payload, Batch).ToArray();
    }

    [GlobalSetup(Targets = [nameof(V2Latency), nameof(V2Throughput)])]
    public async Task SetupV2Async()
    {
        CreatePayloads();
        _transport = new InMemoryMessageTransport();
        _host = await BenchHost.StartAsync(s => s.AddFoundatioMessaging(m => m
            .UseTransport(_transport)
            .MapMessage<BenchPayload>("bench.payload")
            .AddConsumer<BenchPayload>((_, _, _) =>
            {
                _countdown.Signal();
                return Task.CompletedTask;
            }, new HandlerOptions { MaxConcurrency = Concurrency })));
        _client = _host.Services.GetRequiredService<IMessageClient>();
    }

    [GlobalSetup(Targets = [nameof(LegacyQueueLatency), nameof(LegacyQueueThroughput)])]
    public void SetupLegacyQueue()
    {
        CreatePayloads();
        _legacyQueue = new InMemoryQueue<BenchPayload>();
        _legacyWorkers = new LegacyQueueWorkers(_legacyQueue, Concurrency, _ =>
        {
            _countdown.Signal();
            return JobResult.Success;
        });
    }

    [GlobalSetup(Targets = [nameof(LegacyBusLatency), nameof(LegacyBusThroughput)])]
    public async Task SetupLegacyBusAsync()
    {
        CreatePayloads();
        _legacyBus = new InMemoryMessageBus();
        await _legacyBus.SubscribeAsync<BenchPayload>(_ => _countdown.Signal());
    }

    [GlobalSetup(Targets = [nameof(FloorLatency), nameof(FloorThroughput)])]
    public void SetupFloor()
    {
        CreatePayloads();
        _floor = new ChannelFloor(Concurrency, _ => _countdown.Signal());
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_host is not null)
            await _host.DisposeAsync();
        if (_transport is not null)
            await _transport.DisposeAsync();
        if (_legacyWorkers is not null)
            await _legacyWorkers.DisposeAsync();
        _legacyQueue?.Dispose();
        _legacyBus?.Dispose();
        if (_floor is not null)
            await _floor.DisposeAsync();
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Latency")]
    public async Task V2Latency()
    {
        var done = _countdown.Reset(1);
        await _client.SendAsync(_payload);
        await done;
    }

    [Benchmark]
    [BenchmarkCategory("Latency")]
    public async Task LegacyQueueLatency()
    {
        var done = _countdown.Reset(1);
        await _legacyQueue.EnqueueAsync(_payload);
        await done;
    }

    [Benchmark]
    [BenchmarkCategory("Latency")]
    public async Task LegacyBusLatency()
    {
        var done = _countdown.Reset(1);
        await _legacyBus.PublishAsync(_payload);
        await done;
    }

    [Benchmark]
    [BenchmarkCategory("Latency")]
    public async Task FloorLatency()
    {
        var done = _countdown.Reset(1);
        _floor.Send(_payload);
        await done;
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Batch)]
    [BenchmarkCategory("Throughput")]
    public async Task V2Throughput()
    {
        var done = _countdown.Reset(Batch);
        await _client.SendBatchAsync(_batch);
        await done.WaitOrThrowAsync(nameof(V2Throughput));
    }

    [Benchmark(OperationsPerInvoke = Batch)]
    [BenchmarkCategory("Throughput")]
    public async Task LegacyQueueThroughput()
    {
        var done = _countdown.Reset(Batch);
        foreach (var payload in _batch)
            await _legacyQueue.EnqueueAsync(payload);

        await done.WaitOrThrowAsync(nameof(LegacyQueueThroughput));
    }

    [Benchmark(OperationsPerInvoke = Batch)]
    [BenchmarkCategory("Throughput")]
    public async Task LegacyBusThroughput()
    {
        var done = _countdown.Reset(Batch);
        int perPublisher = Batch / Concurrency, extra = Batch % Concurrency;
        await Task.WhenAll(Enumerable.Range(0, Concurrency).Select(p => Task.Run(async () =>
        {
            int count = perPublisher + (p < extra ? 1 : 0);
            for (int i = 0; i < count; i++)
                await _legacyBus.PublishAsync(_payload);
        })));

        await done.WaitOrThrowAsync(nameof(LegacyBusThroughput));
    }

    [Benchmark(OperationsPerInvoke = Batch)]
    [BenchmarkCategory("Throughput")]
    public async Task FloorThroughput()
    {
        var done = _countdown.Reset(Batch);
        foreach (var payload in _batch)
            _floor.Send(payload);

        await done.WaitOrThrowAsync(nameof(FloorThroughput));
    }
}

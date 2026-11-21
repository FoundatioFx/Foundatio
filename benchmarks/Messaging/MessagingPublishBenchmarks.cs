using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace Foundatio.Benchmarks.Messaging;

/// <summary>
/// The cost of sending a message, without handling it: <see cref="IMessageClient.SendAsync{T}"/> into the in-memory
/// transport with nothing receiving, against the floor of serializing the body and writing it to a channel. The legacy
/// in-memory bus has no send without delivery (it returns early when nothing subscribes), so its row delivers each
/// message to one no-op subscriber, and is an upper bound for the legacy send cost. Each operation is one message.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("Messaging")]
public class MessagingPublishBenchmarks
{
    private const int Batch = 1000;
    private static readonly MessageSource Queue = MessageSource.Queue("bench.payload");

    private InMemoryMessageTransport _transport;
    private BenchHost _host;
    private IMessageClient _client;
    private InMemoryMessageBus _legacyBus;
    private readonly Channel<byte[]> _floor = Channel.CreateUnbounded<byte[]>();
    private BenchPayload _payload;

    [Params(100, 16384)]
    public int PayloadBytes { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _payload = BenchPayload.Create(PayloadBytes);
        _transport = new InMemoryMessageTransport();
        _host = await BenchHost.StartAsync(s => s.AddFoundatioMessaging(m => m.UseTransport(_transport).MapMessage<BenchPayload>("bench.payload")));
        _client = _host.Services.GetRequiredService<IMessageClient>();

        _legacyBus = new InMemoryMessageBus();
        await _legacyBus.SubscribeAsync<BenchPayload>(_ => { });
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _host.DisposeAsync();
        await _transport.DisposeAsync();
        _legacyBus.Dispose();
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Batch)]
    public async Task V2Send()
    {
        for (int i = 0; i < Batch; i++)
            await _client.SendAsync(_payload);

        await _transport.DeleteAsync(Queue);
    }

    [Benchmark(OperationsPerInvoke = Batch)]
    public async Task LegacyBusPublishToOneSubscriber()
    {
        for (int i = 0; i < Batch; i++)
            await _legacyBus.PublishAsync(_payload);
    }

    [Benchmark(OperationsPerInvoke = Batch)]
    public int FloorSerializeAndWrite()
    {
        for (int i = 0; i < Batch; i++)
            _floor.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(_payload));

        int drained = 0;
        while (_floor.Reader.TryRead(out _))
            drained++;

        return drained;
    }
}

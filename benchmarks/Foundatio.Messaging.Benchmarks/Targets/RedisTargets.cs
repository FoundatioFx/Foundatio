using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Queues;
using StackExchange.Redis;

namespace Foundatio.Messaging.Benchmarks.Targets;

public static class RedisTargets
{
    public static IBenchTarget Legacy(BenchOptions options, RedisCalls calls) => options.Pattern == Pattern.Queue
        ? new LegacyQueueTarget(options, () => new RedisQueue<BenchMessage>(o => o.ConnectionMultiplexer(calls.Connection).Name(options.Name)))
        : new LegacyBusTarget(options, () => new RedisMessageBus(o => o.Subscriber(calls.Connection.GetSubscriber()).Topic(options.Name)), durable: false);
}

/// <summary>
/// Redis Streams by hand: XADD (pipelined for batches), XREADGROUP of up to 100, XACK in batches. No leases, delays,
/// retries or dead letters, so it is the floor for one durable consumer group.
/// </summary>
public sealed class RawRedisTarget(BenchOptions options, RedisCalls calls) : BenchTargetBase(options)
{
    private const string Group = "bench";
    private readonly RedisKey _key = $"{{raw-{options.Name}}}:stream";
    private RawPump<StreamEntry>? _pump;
    private IDatabase Database => calls.Connection.GetDatabase();

    public override async Task InitAsync(CancellationToken cancellationToken)
    {
        await Database.StreamCreateConsumerGroupAsync(_key, Group, StreamPosition.NewMessages, createStream: true);
    }

    public override Task SendAsync(BenchMessage message, CancellationToken cancellationToken) =>
        Database.StreamAddAsync(_key, "d", JsonSerializer.SerializeToUtf8Bytes(message));

    public override async Task SendBatchAsync(IReadOnlyList<BenchMessage> messages, CancellationToken cancellationToken)
    {
        var batch = Database.CreateBatch();
        var adds = messages.Select(m => batch.StreamAddAsync(_key, "d", JsonSerializer.SerializeToUtf8Bytes(m))).ToList();
        batch.Execute();
        await Task.WhenAll(adds);
    }

    public override Task StartAsync(int concurrency, Func<BenchMessage, ValueTask> handler, CancellationToken cancellationToken)
    {
        string consumer = "c-" + Guid.NewGuid().ToString("N")[..6];
        _pump = new RawPump<StreamEntry>(
            async (max, _) => await Database.StreamReadGroupAsync(_key, Group, consumer, StreamPosition.NewMessages, max),
            entry => JsonSerializer.Deserialize<BenchMessage>((byte[])entry["d"]!)!,
            async entries => await Database.StreamAcknowledgeAsync(_key, Group, entries.Select(e => e.Id).ToArray()),
            maxBatch: 100,
            receivers: 1);
        _pump.Start(concurrency, handler);
        return Task.CompletedTask;
    }

    public override async Task StopAsync()
    {
        if (_pump is not null)
            await _pump.DisposeAsync();
        _pump = null;
    }

    public override Task CleanupAsync() => Database.KeyDeleteAsync(_key);
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Foundatio.Queues;

namespace Foundatio.Messaging.Benchmarks.Targets;

public static class AzureStorageTargets
{
    public static IBenchTarget Legacy(BenchOptions options) => options.Pattern == Pattern.Queue
        ? new LegacyQueueTarget(options, () => new AzureStorageQueue<BenchMessage>(o => o.ConnectionString(options.AzureStorage).Name("legacy-" + options.Name).DequeueInterval(TimeSpan.FromMilliseconds(100))))
        : throw new ArgumentException("Foundatio.AzureStorage has no legacy message bus.");
}

/// <summary>
/// The Storage Queues SDK by hand: <c>SendMessage</c> per message, receives of 32 (one per 32 handlers plus one, at most
/// 16) polling every 1-20 ms when empty, and one <c>DeleteMessage</c> per message, concurrently, since Storage has no
/// batch delete. The floor for one queue.
/// </summary>
public sealed class RawAzureStorageTarget(BenchOptions options) : BenchTargetBase(options)
{
    private readonly QueueClient _queue = new(options.AzureStorage, "raw-" + options.Name, new QueueClientOptions { MessageEncoding = QueueMessageEncoding.None });
    private RawPump<QueueMessage>? _pump;

    public override async Task InitAsync(CancellationToken cancellationToken)
    {
        await _queue.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
    }

    public override Task SendAsync(BenchMessage message, CancellationToken cancellationToken) =>
        _queue.SendMessageAsync(JsonSerializer.Serialize(message), cancellationToken);

    public override Task SendBatchAsync(IReadOnlyList<BenchMessage> messages, CancellationToken cancellationToken) =>
        Parallel.ForEachAsync(messages, new ParallelOptions { MaxDegreeOfParallelism = 32, CancellationToken = cancellationToken },
            async (message, token) => await _queue.SendMessageAsync(JsonSerializer.Serialize(message), token));

    public override Task StartAsync(int concurrency, Func<BenchMessage, ValueTask> handler, CancellationToken cancellationToken)
    {
        _pump = new RawPump<QueueMessage>(
            async (max, token) => (await _queue.ReceiveMessagesAsync(Math.Min(max, 32), TimeSpan.FromSeconds(30), token)).Value,
            message => JsonSerializer.Deserialize<BenchMessage>(message.Body.ToString())!,
            messages => Task.WhenAll(messages.Select(m => _queue.DeleteMessageAsync(m.MessageId, m.PopReceipt))),
            maxBatch: 32,
            receivers: Math.Clamp((concurrency + 31) / 32 + 1, 1, 16));
        _pump.Start(concurrency, handler);
        return Task.CompletedTask;
    }

    public override async Task StopAsync()
    {
        if (_pump is not null)
            await _pump.DisposeAsync();
        _pump = null;
    }

    public override async Task CleanupAsync()
    {
        await _queue.DeleteIfExistsAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await StopAsync();
        await base.DisposeAsync();
    }
}

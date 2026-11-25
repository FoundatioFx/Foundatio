using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Foundatio.Queues;

namespace Foundatio.Messaging.Benchmarks.Targets;

public static class SqsTargets
{
    public static IBenchTarget Legacy(BenchOptions options) => options.Pattern == Pattern.Queue
        ? new LegacyQueueTarget(options, () => new SQSQueue<BenchMessage>(o => o.ConnectionString(options.Sqs).Name("legacy-" + options.Name)))
        : new LegacyBusTarget(options, () => new SQSMessageBus(o => o.ConnectionString(options.Sqs).Topic("legacy-" + options.Name).UseDurableSubscription("legacy-" + options.Name + "-sub")), durable: true);
}

/// <summary>
/// The SQS SDK by hand: <c>SendMessage</c> / <c>SendMessageBatch</c> of 10, receives of 10 with a 20 s long poll (one
/// per 10 handlers, at most 16), deletes batched by 10. The floor for one standard queue.
/// </summary>
public sealed class RawSqsTarget(BenchOptions options) : BenchTargetBase(options)
{
    private readonly AmazonSQSClient _client = CreateClient(options.Sqs);
    private readonly string _queueName = "raw-" + options.Name;
    private string? _queueUrl;
    private RawPump<Amazon.SQS.Model.Message>? _pump;

    private static AmazonSQSClient CreateClient(string connectionString)
    {
        var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).ToDictionary(p => p[0].ToLowerInvariant(), p => p[1]);
        return new AmazonSQSClient(new BasicAWSCredentials(parts["accesskey"], parts["secretkey"]), new AmazonSQSConfig { ServiceURL = parts["serviceurl"], AuthenticationRegion = "us-east-1" });
    }

    public override async Task InitAsync(CancellationToken cancellationToken)
    {
        _queueUrl = (await _client.CreateQueueAsync(_queueName, cancellationToken)).QueueUrl;
    }

    public override Task SendAsync(BenchMessage message, CancellationToken cancellationToken) =>
        _client.SendMessageAsync(new SendMessageRequest(_queueUrl, JsonSerializer.Serialize(message)), cancellationToken);

    public override Task SendBatchAsync(IReadOnlyList<BenchMessage> messages, CancellationToken cancellationToken) =>
        Task.WhenAll(messages.Chunk(10).Select(chunk => _client.SendMessageBatchAsync(new SendMessageBatchRequest(_queueUrl,
            chunk.Select((m, i) => new SendMessageBatchRequestEntry(i.ToString(), JsonSerializer.Serialize(m))).ToList()), cancellationToken)));

    public override Task StartAsync(int concurrency, Func<BenchMessage, ValueTask> handler, CancellationToken cancellationToken)
    {
        _pump = new RawPump<Amazon.SQS.Model.Message>(
            async (max, token) => (IReadOnlyList<Amazon.SQS.Model.Message>)((await _client.ReceiveMessageAsync(new ReceiveMessageRequest(_queueUrl) { MaxNumberOfMessages = Math.Min(max, 10), WaitTimeSeconds = 20 }, token)).Messages ?? []),
            message => JsonSerializer.Deserialize<BenchMessage>(message.Body)!,
            messages => Task.WhenAll(messages.Chunk(10).Select(chunk => _client.DeleteMessageBatchAsync(new DeleteMessageBatchRequest(_queueUrl,
                chunk.Select((m, i) => new DeleteMessageBatchRequestEntry(i.ToString(), m.ReceiptHandle)).ToList())))),
            maxBatch: 10,
            receivers: Math.Clamp((concurrency + 9) / 10 + 1, 1, 16));
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
        if (_queueUrl is not null)
            await _client.DeleteQueueAsync(_queueUrl);
    }

    public override async ValueTask DisposeAsync()
    {
        await StopAsync();
        _client.Dispose();
        await base.DisposeAsync();
    }
}

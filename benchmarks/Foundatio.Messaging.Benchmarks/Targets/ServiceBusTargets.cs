using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Foundatio.Queues;

namespace Foundatio.Messaging.Benchmarks.Targets;

public static class ServiceBusTargets
{
    public static IBenchTarget Legacy(BenchOptions options) => options.Pattern == Pattern.Queue
        ? new LegacyQueueTarget(options, () => new AzureServiceBusQueue<BenchMessage>(o => o.ConnectionString(options.ServiceBus).Name("legacy-" + options.Name)))
        : new LegacyBusTarget(options, () => new AzureServiceBusMessageBus(o => o.ConnectionString(options.ServiceBus).Topic("legacy-" + options.Name).SubscriptionName("bench")), durable: true);
}

/// <summary>
/// The Service Bus SDK by hand: <c>ServiceBusProcessor</c> with <c>MaxConcurrentCalls</c> = handlers and automatic
/// completion, sends one at a time or in one <c>SendMessagesAsync</c>. The floor for one queue.
/// </summary>
public sealed class RawServiceBusTarget(BenchOptions options) : BenchTargetBase(options)
{
    private readonly ServiceBusClient _client = new(options.ServiceBus);
    private readonly string _queue = "raw-" + options.Name;
    private ServiceBusSender? _sender;
    private ServiceBusProcessor? _processor;

    private ServiceBusSender Sender => _sender ??= _client.CreateSender(_queue);

    public override async Task InitAsync(CancellationToken cancellationToken)
    {
        var admin = new ServiceBusAdministrationClient(Options.ServiceBus.Replace(":5672", ":5300"));
        if (!await admin.QueueExistsAsync(_queue, cancellationToken))
            await admin.CreateQueueAsync(new CreateQueueOptions(_queue) { MaxDeliveryCount = 10 }, cancellationToken);
    }

    public override Task SendAsync(BenchMessage message, CancellationToken cancellationToken) =>
        Sender.SendMessageAsync(new ServiceBusMessage(JsonSerializer.SerializeToUtf8Bytes(message)), cancellationToken);

    public override Task SendBatchAsync(IReadOnlyList<BenchMessage> messages, CancellationToken cancellationToken) =>
        Sender.SendMessagesAsync(messages.Select(m => new ServiceBusMessage(JsonSerializer.SerializeToUtf8Bytes(m))), cancellationToken);

    public override async Task StartAsync(int concurrency, Func<BenchMessage, ValueTask> handler, CancellationToken cancellationToken)
    {
        _processor = _client.CreateProcessor(_queue, new ServiceBusProcessorOptions { MaxConcurrentCalls = concurrency, AutoCompleteMessages = true });
        _processor.ProcessMessageAsync += async args => await handler(JsonSerializer.Deserialize<BenchMessage>(args.Message.Body.ToMemory().Span)!);
        _processor.ProcessErrorAsync += args =>
        {
            Console.Error.WriteLine($"raw processor error: {args.Exception.Message}");
            return Task.CompletedTask;
        };
        await _processor.StartProcessingAsync(cancellationToken);
    }

    public override async Task StopAsync()
    {
        if (_processor is not null)
            await _processor.DisposeAsync();
        _processor = null;
    }

    public override async Task CleanupAsync()
    {
        var admin = new ServiceBusAdministrationClient(Options.ServiceBus.Replace(":5672", ":5300"));
        await admin.DeleteQueueAsync(_queue);
    }

    public override async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _client.DisposeAsync();
        await base.DisposeAsync();
    }
}

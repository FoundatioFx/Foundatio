using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
using Foundatio.Messaging;
using Foundatio.Tests.Extensions;
using Xunit;

namespace Foundatio.Tests.Messaging;

public class DeliveryIntentTests
{
    [Fact]
    public async Task SubscribeAsync_SentOnly_IgnoresPublishedMessagesAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var bus = new MessageBus(transport);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        var received = new ConcurrentQueue<string?>();
        var sentSignal = new AsyncCountdownEvent(1);
        await using var subscription = await bus.SubscribeAsync<IntentEvent>((message, _) =>
        {
            received.Enqueue(message.Message.Data);
            sentSignal.Signal();
            return Task.CompletedTask;
        }, new MessageSubscriptionOptions { Deliveries = MessageDeliveries.Sent }, cts.Token);

        Assert.Equal("", subscription.Source); // no publish channel was wired
        Assert.NotEqual("", subscription.Destination);

        // A published event must not reach a sent-only handler (its group does not exist), and the command must.
        await bus.PublishAsync(new IntentEvent { Data = "event" }, cancellationToken: cancellationToken);
        await bus.SendAsync(new IntentEvent { Data = "command" }, cancellationToken: cancellationToken);

        await sentSignal.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken); // let any (incorrect) event delivery arrive

        Assert.Equal(["command"], received);
    }

    [Fact]
    public async Task SubscribeAsync_PublishedOnly_IgnoresSentMessagesAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var bus = new MessageBus(transport);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        var received = new ConcurrentQueue<string?>();
        var publishedSignal = new AsyncCountdownEvent(1);
        await using var subscription = await bus.SubscribeAsync<IntentEvent>((message, _) =>
        {
            received.Enqueue(message.Message.Data);
            publishedSignal.Signal();
            return Task.CompletedTask;
        }, new MessageSubscriptionOptions { Deliveries = MessageDeliveries.Published }, cts.Token);

        Assert.Equal("", subscription.Destination); // no send channel was wired
        Assert.NotEqual("", subscription.Source);

        // The command sits unconsumed on its queue (this handler never attached to it); the event must arrive.
        await bus.SendAsync(new IntentEvent { Data = "command" }, cancellationToken: cancellationToken);
        await bus.PublishAsync(new IntentEvent { Data = "event" }, cancellationToken: cancellationToken);

        await publishedSignal.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken); // let any (incorrect) command delivery arrive

        Assert.Equal(["event"], received);

        var queueStats = await transport.GetStatsAsync(DestinationAddress.ForQueue("intent-event"), cancellationToken);
        Assert.Equal(1, queueStats.Queued); // the command is still parked on the routed queue, untouched
    }

    [Fact]
    public async Task SubscribeAsync_ExplicitPublished_OnQueueOnlyTransport_ThrowsAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var bus = new MessageBus(new QueueOnlyTransport());

        await Assert.ThrowsAsync<NotSupportedException>(() => bus.SubscribeAsync<IntentEvent>(
            (_, _) => Task.CompletedTask,
            new MessageSubscriptionOptions { Deliveries = MessageDeliveries.Published },
            cancellationToken));
    }

    [Fact]
    public async Task SubscribeAsync_DefaultBoth_OnQueueOnlyTransport_WiresSendChannelOnlyAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = new QueueOnlyTransport();
        await using var bus = new MessageBus(transport);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        var received = new AsyncCountdownEvent(1);
        await using var subscription = await bus.SubscribeAsync<IntentEvent>((message, _) =>
        {
            Assert.Equal("command", message.Message.Data);
            received.Signal();
            return Task.CompletedTask;
        }, cancellationToken: cts.Token);

        Assert.NotEqual("", subscription.Destination);
        Assert.Equal("", subscription.Source); // the publish channel was skipped, not faked

        await bus.SendAsync(new IntentEvent { Data = "command" }, cancellationToken: cancellationToken);
        await received.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class IntentEvent
    {
        public string? Data { get; set; }
    }

    // A transport that truly has no topic/subscription support, so the bus must not wire (or fake) a publish channel.
    private sealed class QueueOnlyTransport : IMessageTransport, ISupportsPull, ITransportInfo
    {
        private readonly ConcurrentDictionary<string, ConcurrentQueue<TransportMessage>> _queues = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TransportMessage> _inFlight = new(StringComparer.Ordinal);

        public DeliveryGuarantee DeliveryGuarantee => DeliveryGuarantee.AtLeastOnce;
        public IReadOnlySet<DestinationRole> SupportedRoles => new HashSet<DestinationRole> { DestinationRole.Queue };
        public TransportCapabilities GetCapabilities(DestinationRole role) => TransportCapabilities.None;

        public Task<SendResult> SendAsync(DestinationAddress destination, IReadOnlyList<TransportMessage> messages, TransportSendOptions options, CancellationToken ct = default)
        {
            if (destination.Role != DestinationRole.Queue)
                throw new NotSupportedException("Queues only.");

            var queue = _queues.GetOrAdd(destination.Key, static _ => new ConcurrentQueue<TransportMessage>());
            var items = new List<SendItemResult>(messages.Count);
            foreach (var message in messages)
            {
                string id = message.MessageId ?? Guid.NewGuid().ToString("N");
                queue.Enqueue(message with { MessageId = id });
                items.Add(new SendItemResult { MessageId = id });
            }

            return Task.FromResult(new SendResult { Items = items });
        }

        public async Task<IReadOnlyList<TransportEntry>> ReceiveAsync(DestinationAddress source, ReceiveRequest request, CancellationToken ct)
        {
            var queue = _queues.GetOrAdd(source.Key, static _ => new ConcurrentQueue<TransportMessage>());
            var deadline = request.MaxWaitTime is { } wait && wait > TimeSpan.Zero ? DateTimeOffset.UtcNow.Add(wait) : DateTimeOffset.UtcNow;
            var entries = new List<TransportEntry>();
            int max = Math.Max(1, request.MaxMessages);

            while (true)
            {
                while (entries.Count < max && queue.TryDequeue(out var message))
                {
                    string token = Guid.NewGuid().ToString("N");
                    _inFlight[token] = message;
                    entries.Add(new TransportEntry
                    {
                        Id = message.MessageId!,
                        Destination = source,
                        Body = message.Body,
                        Headers = message.Headers,
                        Receipt = new Receipt { TransportState = token }
                    });
                }

                if (entries.Count > 0 || DateTimeOffset.UtcNow >= deadline)
                    return entries;

                await Task.Delay(TimeSpan.FromMilliseconds(15), ct).ConfigureAwait(false);
            }
        }

        public Task CompleteAsync(TransportEntry entry, CancellationToken ct = default)
        {
            if (entry.Receipt.TransportState is not string token || !_inFlight.TryRemove(token, out _))
                throw new ReceiptExpiredException();

            return Task.CompletedTask;
        }

        public Task AbandonAsync(TransportEntry entry, CancellationToken ct = default)
        {
            if (entry.Receipt.TransportState is not string token || !_inFlight.TryRemove(token, out var message))
                throw new ReceiptExpiredException();

            _queues.GetOrAdd(entry.Destination.Key, static _ => new ConcurrentQueue<TransportMessage>()).Enqueue(message);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

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
    public async Task ConsumeAsync_MultipleFallbackTypesOnSameEndpoint_RejectsAmbiguousDispatchAsync()
    {
        var token = TestContext.Current.CancellationToken;
        await using var bus = new MessageBus(new InMemoryMessageTransport());
        var options = new MessageConsumerOptions { Destination = "shared" };
        await using var first = await bus.ConsumeAsync<ICloneable>((_, _) => Task.CompletedTask, options, token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bus.ConsumeAsync<IDisposable>((_, _) => Task.CompletedTask, options, token));
    }

    [Fact]
    public async Task ReceiveAsync_WithoutHandler_CanSettleOrReturnWorkAsync()
    {
        var token = TestContext.Current.CancellationToken;
        await using var bus = new MessageBus(new InMemoryMessageTransport());
        string id = await bus.SendAsync(new IntentEvent(), cancellationToken: token);
        await using (var first = await bus.ReceiveAsync<IntentEvent>(cancellationToken: token))
        {
            Assert.NotNull(first);
            Assert.Equal(id, first.Id);
        }

        await using var second = await bus.ReceiveAsync<IntentEvent>(cancellationToken: token);
        Assert.NotNull(second);
        Assert.Equal(id, second.Id);
        Assert.Equal(2, second.Attempts);
        await second.CompleteAsync(token);
        Assert.Null(await bus.ReceiveAsync<IntentEvent>(cancellationToken: token));
    }

    [Fact]
    public async Task ConsumeAsync_IgnoresPublishedMessagesAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var bus = new MessageBus(transport);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        var received = new ConcurrentQueue<string?>();
        var sentSignal = new AsyncCountdownEvent(1);
        await using var subscription = await bus.ConsumeAsync<IntentEvent>((message, _) =>
        {
            received.Enqueue(message.Message.Data);
            sentSignal.Signal();
            return Task.CompletedTask;
        }, new MessageConsumerOptions(), cts.Token);

        Assert.Equal(DestinationRole.Queue, subscription.Source.Role);

        // A published event must not reach a sent-only handler (its group does not exist), and the command must.
        await bus.PublishAsync(new IntentEvent { Data = "event" }, cancellationToken: cancellationToken);
        await bus.SendAsync(new IntentEvent { Data = "command" }, cancellationToken: cancellationToken);

        await sentSignal.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken); // let any (incorrect) event delivery arrive

        Assert.Equal(["command"], received);
    }

    [Fact]
    public async Task SubscribeAsync_IgnoresSentMessagesAsync()
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
        }, new MessageSubscriptionOptions(), cts.Token);

        Assert.Equal(DestinationRole.Subscription, subscription.Source.Role);

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
    public async Task SubscribeAsync_OnQueueOnlyTransport_ThrowsAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var bus = new MessageBus(new QueueOnlyTransport());

        await Assert.ThrowsAsync<NotSupportedException>(() => bus.SubscribeAsync<IntentEvent>(
            (_, _) => Task.CompletedTask,
            new MessageSubscriptionOptions(),
            cancellationToken));
    }

    [Fact]
    public async Task ConsumeAsync_OnQueueOnlyTransport_ReceivesCommandsAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = new QueueOnlyTransport();
        await using var bus = new MessageBus(transport);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        var received = new AsyncCountdownEvent(1);
        await using var subscription = await bus.ConsumeAsync<IntentEvent>((message, _) =>
        {
            Assert.Equal("command", message.Message.Data);
            received.Signal();
            return Task.CompletedTask;
        }, cancellationToken: cts.Token);

        Assert.Equal(DestinationRole.Queue, subscription.Source.Role);

        await bus.SendAsync(new IntentEvent { Data = "command" }, cancellationToken: cancellationToken);
        await received.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ConsumeAsync_DuplicateHandlerForSameQueueAndType_ThrowsAsync()
    {
        var token = TestContext.Current.CancellationToken;
        await using var bus = new MessageBus(new InMemoryMessageTransport());
        await using var consumer = await bus.ConsumeAsync<IntentEvent>((_, _) => Task.CompletedTask, cancellationToken: token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bus.ConsumeAsync<IntentEvent>((_, _) => Task.CompletedTask, cancellationToken: token));
    }

    [Fact]
    public async Task SubscribeAsync_UnnamedSubscription_DisposalDeletesResourceAsync()
    {
        var token = TestContext.Current.CancellationToken;
        var transport = new InMemoryMessageTransport();
        await using var bus = new MessageBus(transport);
        var subscription = await bus.SubscribeAsync<IntentEvent>((_, _) => Task.CompletedTask, cancellationToken: token);
        var source = subscription.Source;
        Assert.True(await transport.ExistsAsync(source, token));
        await subscription.DisposeAsync();
        Assert.False(await transport.ExistsAsync(source, token));
    }

    [Fact]
    public async Task SubscribeAsync_NamedSubscription_DisposalPreservesBacklogAsync()
    {
        var token = TestContext.Current.CancellationToken;
        var transport = new InMemoryMessageTransport();
        await using var bus = new MessageBus(transport);
        var subscription = await bus.SubscribeAsync<IntentEvent>((_, _) => Task.CompletedTask, new() { Subscription = "billing" }, token);
        var source = subscription.Source;
        await subscription.DisposeAsync();
        await bus.PublishAsync(new IntentEvent(), cancellationToken: token);
        Assert.True(await transport.ExistsAsync(source, token));
        Assert.Equal(1, (await transport.GetStatsAsync(source, token)).Queued);
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
        public TransportCapabilities GetCapabilities(DestinationAddress destination) => TransportCapabilities.None;

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

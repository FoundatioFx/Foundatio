using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Foundatio.Tests.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Foundatio.Tests.Messaging;

public class PubSubTests
{
    [Fact]
    public async Task PublishAsync_FansOutToMultipleSubscriptionsAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var pubSub = new MessageBus(transport);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        var firstReceived = new AsyncCountdownEvent(1);
        var secondReceived = new AsyncCountdownEvent(1);

        await using var first = await pubSub.SubscribeAsync<PreviewEvent>((message, _) =>
        {
            Assert.Equal("published", message.Message.Data);
            firstReceived.Signal();
            return Task.CompletedTask;
        }, new MessageSubscriptionOptions { Subscription = "subscriber-a" }, cts.Token);

        await using var second = await pubSub.SubscribeAsync<PreviewEvent>((message, _) =>
        {
            Assert.Equal("published", message.Message.Data);
            secondReceived.Signal();
            return Task.CompletedTask;
        }, new MessageSubscriptionOptions { Subscription = "subscriber-b" }, cts.Token);

        await pubSub.PublishAsync(new PreviewEvent { Data = "published" }, cancellationToken: cancellationToken);

        await firstReceived.WaitAsync(TimeSpan.FromSeconds(2));
        await secondReceived.WaitAsync(TimeSpan.FromSeconds(2));
        var firstStats = await transport.GetStatsAsync(DestinationAddress.ForSubscription(first.Topic, first.Subscription), cancellationToken);
        var secondStats = await transport.GetStatsAsync(DestinationAddress.ForSubscription(second.Topic, second.Subscription), cancellationToken);
        Assert.Equal(1, firstStats.Completed);
        Assert.Equal(1, secondStats.Completed);
    }

    [Fact]
    public async Task SubscribeAsync_WithSameSubscriptionAndDifferentKeys_CompetesOnTransportSubscriptionAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var pubSub = new MessageBus(transport);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var received = new AsyncCountdownEvent(2);
        var deliveriesByMessageId = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

        Func<IMessageContext<PreviewEvent>, CancellationToken, Task> handler = (message, _) =>
        {
            deliveriesByMessageId.AddOrUpdate(message.Id, 1, (_, count) => count + 1);
            received.Signal();
            return Task.CompletedTask;
        };

        await using var first = await pubSub.SubscribeAsync(handler, new MessageSubscriptionOptions
        {
            Subscription = "billing-service",
            Key = "node-a"
        }, cts.Token);
        await using var second = await pubSub.SubscribeAsync(handler, new MessageSubscriptionOptions
        {
            Subscription = "billing-service",
            Key = "node-b"
        }, cts.Token);

        await pubSub.PublishBatchAsync([
            new PreviewEvent { Data = "one" },
            new PreviewEvent { Data = "two" }
        ], cancellationToken: cancellationToken);

        await received.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForCompletedAsync(transport, DestinationAddress.ForSubscription(first.Topic, first.Subscription), 2, cancellationToken);

        Assert.Equal(first.Topic, second.Topic);
        Assert.Equal(first.Subscription, second.Subscription);
        Assert.Equal(first.Source, second.Source); // same topic + subscription -> one shared transport source
        Assert.NotEqual(first.Key, second.Key);
        Assert.Equal(2, deliveriesByMessageId.Count);
        Assert.All(deliveriesByMessageId.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task SubscribeAsync_SameSubscriptionOnTwoTopics_IsolatesPerTopicAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var pubSub = new MessageBus(transport);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        var ordersReceived = new List<string?>();
        var paymentsReceived = new List<string?>();
        var ordersSignal = new AsyncCountdownEvent(1);
        var paymentsSignal = new AsyncCountdownEvent(1);

        // The same subscription identity ("shared") on two different topics.
        await using var orders = await pubSub.SubscribeAsync<PreviewEvent>((message, _) =>
        {
            lock (ordersReceived)
                ordersReceived.Add(message.Message.Data);
            ordersSignal.Signal();
            return Task.CompletedTask;
        }, new MessageSubscriptionOptions { Topic = "orders", Subscription = "shared" }, cts.Token);

        await using var payments = await pubSub.SubscribeAsync<PreviewEvent>((message, _) =>
        {
            lock (paymentsReceived)
                paymentsReceived.Add(message.Message.Data);
            paymentsSignal.Signal();
            return Task.CompletedTask;
        }, new MessageSubscriptionOptions { Topic = "payments", Subscription = "shared" }, cts.Token);

        Assert.Equal(orders.Subscription, payments.Subscription); // same logical subscription identity
        Assert.NotEqual(orders.Source, payments.Source);          // but distinct topic-qualified transport sources

        // Publish one message to each topic. Each subscriber must receive only its own topic's message — proving both
        // subscribers are live (not an always-broken one passing a negative-only assertion) and that they are isolated.
        await pubSub.PublishAsync(new PreviewEvent { Data = "to-orders" }, new MessagePublishOptions { Topic = "orders" }, cancellationToken);
        await pubSub.PublishAsync(new PreviewEvent { Data = "to-payments" }, new MessagePublishOptions { Topic = "payments" }, cancellationToken);

        await ordersSignal.WaitAsync(TimeSpan.FromSeconds(2));
        await paymentsSignal.WaitAsync(TimeSpan.FromSeconds(2));

        // Let any (incorrect) cross-topic delivery arrive before asserting each side received only its own message.
        await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);

        Assert.Equal(new[] { "to-orders" }, ordersReceived);
        Assert.Equal(new[] { "to-payments" }, paymentsReceived);
    }

    [Fact]
    public async Task PublishBatchAsync_DeliversAllMessagesAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var pubSub = new MessageBus(transport);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var received = new AsyncCountdownEvent(2);

        await using var subscription = await pubSub.SubscribeAsync<PreviewEvent>((message, _) =>
        {
            Assert.StartsWith("batch-", message.Message.Data);
            received.Signal();
            return Task.CompletedTask;
        }, new MessageSubscriptionOptions { Subscription = "batch-subscription" }, cts.Token);

        await pubSub.PublishBatchAsync([
            new PreviewEvent { Data = "batch-one" },
            new PreviewEvent { Data = "batch-two" }
        ], cancellationToken: cancellationToken);

        await received.WaitAsync(TimeSpan.FromSeconds(2));
        var stats = await transport.GetStatsAsync(DestinationAddress.ForSubscription(subscription.Topic, subscription.Subscription), cancellationToken);
        Assert.Equal(2, stats.Completed);
    }

    [Fact]
    public async Task PublishAsync_WithOptions_PropagatesHeadersAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var pubSub = new MessageBus(new InMemoryMessageTransport());
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var received = new TaskCompletionSource<IMessageContext<PreviewEvent>>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var subscription = await pubSub.SubscribeAsync<PreviewEvent>((message, _) =>
        {
            received.TrySetResult(message);
            return Task.CompletedTask;
        }, new MessageSubscriptionOptions { Subscription = "metadata-subscription" }, cts.Token);

        await pubSub.PublishAsync(new PreviewEvent { Data = "metadata" }, new MessagePublishOptions
        {
            CorrelationId = "corr-456",
            Priority = MessagePriority.High,
            Headers = MessageHeaders.Create([
                new KeyValuePair<string, string>("tenant", "acme")
            ])
        }, cancellationToken);

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken));
        Assert.Equal(received.Task, completed);

        var message = await received.Task;
        Assert.Equal("metadata", message.Message.Data);
        Assert.Equal("corr-456", message.CorrelationId);
        Assert.Equal(MessagePriority.High, message.Priority);
        Assert.Equal("acme", message.Headers["tenant"]);
        Assert.Equal(typeof(PreviewEvent).FullName, message.MessageType);

    }

    [Fact]
    public async Task PublishAsync_WithDelay_SchedulesThroughRuntimeStoreAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new InMemoryJobRuntimeStore();
        await using var transport = new InMemoryMessageTransport();
        await using var pubSub = new MessageBus(transport, new MessageBusOptions { RuntimeStore = store });
        var processor = CreateDispatchProcessor(store, transport);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var received = new AsyncCountdownEvent(1);

        await using var subscription = await pubSub.SubscribeAsync<PreviewEvent>((_, _) =>
        {
            received.Signal();
            return Task.CompletedTask;
        }, new MessageSubscriptionOptions { Subscription = "delayed-subscription" }, cts.Token);

        await pubSub.PublishAsync(new PreviewEvent { Data = "later" }, new MessagePublishOptions { Delay = TimeSpan.FromMinutes(1) }, cancellationToken);

        await Assert.ThrowsAsync<TimeoutException>(async () => await received.WaitAsync(TimeSpan.FromMilliseconds(50)));
        Assert.Equal(1, await processor.RunDueOccurrencesAsync(DateTimeOffset.UtcNow.AddMinutes(2), cancellationToken: cancellationToken));
        await received.WaitAsync(TimeSpan.FromSeconds(2));

    }

    [Fact]
    public async Task SubscribeAsync_WhenHandlerFails_RedeliversAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var pubSub = new MessageBus(transport);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var received = new AsyncCountdownEvent(2);
        int attempts = 0;

        await using var subscription = await pubSub.SubscribeAsync<PreviewEvent>((message, _) =>
        {
            attempts++;
            Assert.Equal(attempts, message.Attempts);
            received.Signal();

            if (attempts == 1)
                throw new InvalidOperationException("try again");

            return Task.CompletedTask;
        }, new MessageSubscriptionOptions { Subscription = "retry-subscription", MaxAttempts = 2 }, cts.Token);

        await pubSub.PublishAsync(new PreviewEvent { Data = "retry" }, cancellationToken: cancellationToken);

        await received.WaitAsync(TimeSpan.FromSeconds(2));
        var stats = await transport.GetStatsAsync(DestinationAddress.ForSubscription(subscription.Topic, subscription.Subscription), cancellationToken);
        Assert.Equal(1, stats.Completed);
        Assert.Equal(1, stats.Abandoned);
    }


    [Fact]
    public async Task SubscribeAsync_WithSameKeyAndSameRegistration_SharesTheUnderlyingConsumerAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var pubSub = new MessageBus(new InMemoryMessageTransport());
        int handled = 0;
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<IMessageContext<PreviewEvent>, CancellationToken, Task> handler = (_, _) =>
        {
            Interlocked.Increment(ref handled);
            received.TrySetResult();
            return Task.CompletedTask;
        };

        // Registering the same key + handler + options twice is idempotent: both handles refer to the one underlying
        // consumer, so a published message is handled exactly once.
        await using var first = await pubSub.SubscribeAsync(handler, new MessageSubscriptionOptions { Subscription = "same-key", Key = "shared" }, cancellationToken);
        await using var second = await pubSub.SubscribeAsync(handler, new MessageSubscriptionOptions { Subscription = "same-key", Key = "shared" }, cancellationToken);

        Assert.Equal(first.Key, second.Key);
        Assert.Equal(first.Source, second.Source);

        await pubSub.PublishAsync(new PreviewEvent { Data = "once" }, cancellationToken: cancellationToken);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        await Task.Delay(250, cancellationToken);
        Assert.Equal(1, Volatile.Read(ref handled));
    }

    [Fact]
    public async Task SubscribeAsync_WithSameKeyAndDifferentHandler_ThrowsAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var pubSub = new MessageBus(new InMemoryMessageTransport());

        await using var first = await pubSub.SubscribeAsync<PreviewEvent>((_, _) => Task.CompletedTask, new MessageSubscriptionOptions { Subscription = "same-key", Key = "shared" }, cancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await pubSub.SubscribeAsync<PreviewEvent>((_, _) => Task.CompletedTask, new MessageSubscriptionOptions { Subscription = "same-key", Key = "shared" }, cancellationToken));
    }

    [Fact]
    public async Task SubscribeAsync_WithSameKeyAndDifferentFailurePolicy_ThrowsAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var pubSub = new MessageBus(new InMemoryMessageTransport());
        Func<IMessageContext<PreviewEvent>, CancellationToken, Task> handler = (_, _) => Task.CompletedTask;

        await using var first = await pubSub.SubscribeAsync(handler, new MessageSubscriptionOptions
        {
            Subscription = "same-key",
            Key = "shared",
            DeadLetterWhen = static ex => ex is InvalidOperationException
        }, cancellationToken);

        // Shared-key subscriptions form ONE competing group; members with different retry/dead-letter LOGIC would
        // settle the same message differently depending on who received it, so a divergent policy must be rejected —
        // by delegate identity, not by mere has-a-policy presence.
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await pubSub.SubscribeAsync(handler, new MessageSubscriptionOptions
            {
                Subscription = "same-key",
                Key = "shared",
                DeadLetterWhen = static ex => ex is ArgumentException
            }, cancellationToken));
    }

    [Fact]
    public async Task SubscribeAsync_WithGroupedTopicAndSubscriptionIdentity_ReceivesRawMessagesAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        var routing = new MessageRoutingOptionsBuilder()
            .MapTopic("order-events", typeof(IGroupedEvent))
            .UseSubscriptionIdentity("billing-service")
            .Build();
        await using var pubSub = new MessageBus(transport, new MessageBusOptions { Router = new DefaultMessageRouter(routing) });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var received = new AsyncCountdownEvent(2);
        var messageTypes = new List<string>();

        await using var subscription = await pubSub.SubscribeAsync((message, _) =>
        {
            lock (messageTypes)
                messageTypes.Add(message.MessageType!);

            received.Signal();
            return Task.CompletedTask;
        }, new MessageSubscriptionOptions { RouteType = typeof(IGroupedEvent) }, cts.Token);

        await pubSub.PublishBatchAsync(new object[]
        {
            new PreviewEvent { Data = "one" },
            new OtherEvent { Data = "two" }
        }, cancellationToken: cancellationToken);

        await received.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("order-events", subscription.Topic);
        Assert.Equal("billing-service", subscription.Subscription);
        Assert.Equal("order-events/billing-service", subscription.Source); // topic-qualified transport source
        Assert.Contains(typeof(PreviewEvent).FullName!, messageTypes);
        Assert.Contains(typeof(OtherEvent).FullName!, messageTypes);

        var stats = await transport.GetStatsAsync(DestinationAddress.ForSubscription(subscription.Topic, subscription.Subscription), cancellationToken);
        Assert.Equal(2, stats.Completed);
    }


    [Fact]
    public async Task PublishAsync_WithDelay_OnTopicWithoutNativeDelay_RoutesThroughRuntimeStoreAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // The AWS SQS/SNS shape: queues honor a native delay (15-minute cap) but topics have none. A delayed publish
        // within the QUEUE ceiling must still route through the runtime store — deciding by transport-wide capability
        // would take the native path and the broker would silently drop the delay.
        var store = new InMemoryJobRuntimeStore();
        await using var transport = new RoleSplitDelayTransport(queueMaxDelay: TimeSpan.FromMinutes(15));
        await using var pubSub = new MessageBus(transport, new MessageBusOptions { RuntimeStore = store });
        var processor = CreateDispatchProcessor(store, transport);

        await pubSub.PublishAsync(new PreviewEvent { Data = "later" }, new MessagePublishOptions { Delay = TimeSpan.FromMinutes(5) }, cancellationToken);

        Assert.Equal(0, transport.SendCount);
        Assert.Equal(1, await processor.RunDueOccurrencesAsync(DateTimeOffset.UtcNow.AddMinutes(10), cancellationToken: cancellationToken));
        Assert.Equal(1, transport.SendCount);
        Assert.Equal(DestinationRole.Topic, transport.LastDestination?.Role);
        Assert.Null(transport.LastSendOptions?.DeliverAt); // the store dispatches it as due; the delay is spent, not forwarded

        // A delayed QUEUE send within the same transport's queue ceiling still uses the native path.
        await pubSub.SendAsync(new PreviewEvent { Data = "soon" }, new MessageSendOptions { Delay = TimeSpan.FromMinutes(5) }, cancellationToken);
        Assert.Equal(2, transport.SendCount);
        Assert.NotNull(transport.LastSendOptions?.DeliverAt);
    }

    private static async Task WaitForCompletedAsync(InMemoryMessageTransport transport, DestinationAddress destination, long expected, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var stats = await transport.GetStatsAsync(destination, cancellationToken);
            if (stats.Completed == expected)
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }

        var finalStats = await transport.GetStatsAsync(destination, cancellationToken);
        Assert.Equal(expected, finalStats.Completed);
    }

    private static JobScheduleProcessor CreateDispatchProcessor(IJobRuntimeStore store, IMessageTransport transport)
    {
        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var worker = new JobWorker(store, serviceProvider, nodeId: "node-a");
        return new JobScheduleProcessor(new InMemoryJobScheduler(), store, worker, nodeId: "node-a", transport: transport);
    }

    // Mirrors AWS SQS/SNS: native delayed delivery on queues only. Topic sends with a future DeliverAt throw, so a
    // silent delay drop cannot hide.
    private sealed class RoleSplitDelayTransport : IMessageTransport, ISupportsPull, ITransportInfo
    {
        private readonly TimeSpan _queueMaxDelay;

        public RoleSplitDelayTransport(TimeSpan queueMaxDelay) => _queueMaxDelay = queueMaxDelay;

        public int SendCount { get; private set; }
        public TransportSendOptions? LastSendOptions { get; private set; }
        public DestinationAddress? LastDestination { get; private set; }

        public DeliveryGuarantee DeliveryGuarantee => DeliveryGuarantee.AtLeastOnce;
        public IReadOnlySet<DestinationRole> SupportedRoles =>
            new HashSet<DestinationRole> { DestinationRole.Queue, DestinationRole.Topic, DestinationRole.Subscription };

        public TransportCapabilities GetCapabilities(DestinationRole role) => role == DestinationRole.Topic
            ? TransportCapabilities.None
            : new TransportCapabilities { DelayedDelivery = true, MaxDeliveryDelay = _queueMaxDelay };

        public Task<SendResult> SendAsync(DestinationAddress destination, IReadOnlyList<TransportMessage> messages, TransportSendOptions options, CancellationToken ct = default)
        {
            if (destination.Role == DestinationRole.Topic && options.DeliverAt is { } deliverAt && deliverAt > DateTimeOffset.UtcNow)
                throw new NotSupportedException("Topics have no native delayed delivery.");

            SendCount += messages.Count;
            LastSendOptions = options;
            LastDestination = destination;
            var items = new SendItemResult[messages.Count];
            for (int i = 0; i < messages.Count; i++)
                items[i] = new SendItemResult { MessageId = messages[i].MessageId ?? Guid.NewGuid().ToString("N") };

            return Task.FromResult(new SendResult { Items = items });
        }

        public Task<IReadOnlyList<TransportEntry>> ReceiveAsync(DestinationAddress source, ReceiveRequest request, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TransportEntry>>([]);

        public Task CompleteAsync(TransportEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task AbandonAsync(TransportEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private interface IGroupedEvent
    {
    }

    private sealed class PreviewEvent : IGroupedEvent
    {
        public string? Data { get; set; }
    }

    private sealed class OtherEvent : IGroupedEvent
    {
        public string? Data { get; set; }
    }
}

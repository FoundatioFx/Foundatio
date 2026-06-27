using System;
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
        await using var pubSub = new PubSub(transport);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        var firstReceived = new AsyncCountdownEvent(1);
        var secondReceived = new AsyncCountdownEvent(1);

        await using var first = await pubSub.SubscribeAsync<PreviewEvent>((message, _) =>
        {
            Assert.Equal("published", message.Message.Data);
            firstReceived.Signal();
            return Task.CompletedTask;
        }, new PubSubSubscriptionOptions { Subscription = "subscriber-a" }, cts.Token);

        await using var second = await pubSub.SubscribeAsync<PreviewEvent>((message, _) =>
        {
            Assert.Equal("published", message.Message.Data);
            secondReceived.Signal();
            return Task.CompletedTask;
        }, new PubSubSubscriptionOptions { Subscription = "subscriber-b" }, cts.Token);

        await pubSub.PublishAsync(new PreviewEvent { Data = "published" }, cancellationToken: cancellationToken);

        await firstReceived.WaitAsync(TimeSpan.FromSeconds(2));
        await secondReceived.WaitAsync(TimeSpan.FromSeconds(2));
        var firstStats = await transport.GetStatsAsync("subscriber-a", cancellationToken);
        var secondStats = await transport.GetStatsAsync("subscriber-b", cancellationToken);
        Assert.Equal(1, firstStats.Completed);
        Assert.Equal(1, secondStats.Completed);
    }

    [Fact]
    public async Task PublishBatchAsync_DeliversAllMessagesAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var pubSub = new PubSub(transport);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var received = new AsyncCountdownEvent(2);

        await using var subscription = await pubSub.SubscribeAsync<PreviewEvent>((message, _) =>
        {
            Assert.StartsWith("batch-", message.Message.Data);
            received.Signal();
            return Task.CompletedTask;
        }, new PubSubSubscriptionOptions { Subscription = "batch-subscription" }, cts.Token);

        await pubSub.PublishBatchAsync([
            new PreviewEvent { Data = "batch-one" },
            new PreviewEvent { Data = "batch-two" }
        ], cancellationToken: cancellationToken);

        await received.WaitAsync(TimeSpan.FromSeconds(2));
        var stats = await transport.GetStatsAsync("batch-subscription", cancellationToken);
        Assert.Equal(2, stats.Completed);
    }

    [Fact]
    public async Task PublishAsync_WithOptions_PropagatesHeadersAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var pubSub = new PubSub(new InMemoryMessageTransport());
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var received = new TaskCompletionSource<IReceivedMessage<PreviewEvent>>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var subscription = await pubSub.SubscribeAsync<PreviewEvent>((message, _) =>
        {
            received.TrySetResult(message);
            return Task.CompletedTask;
        }, new PubSubSubscriptionOptions { Subscription = "metadata-subscription" }, cts.Token);

        await pubSub.PublishAsync(new PreviewEvent { Data = "metadata" }, new PubSubMessageOptions
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
        await using var pubSub = new PubSub(transport, new PubSubOptions { RuntimeStore = store });
        var processor = CreateDispatchProcessor(store, transport);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var received = new AsyncCountdownEvent(1);

        await using var subscription = await pubSub.SubscribeAsync<PreviewEvent>((_, _) =>
        {
            received.Signal();
            return Task.CompletedTask;
        }, new PubSubSubscriptionOptions { Subscription = "delayed-subscription" }, cts.Token);

        await pubSub.PublishAsync(new PreviewEvent { Data = "later" }, new PubSubMessageOptions { Delay = TimeSpan.FromMinutes(1) }, cancellationToken);

        await Assert.ThrowsAsync<TimeoutException>(async () => await received.WaitAsync(TimeSpan.FromMilliseconds(50)));
        Assert.Equal(1, await processor.RunDueOccurrencesAsync(DateTimeOffset.UtcNow.AddMinutes(2), cancellationToken: cancellationToken));
        await received.WaitAsync(TimeSpan.FromSeconds(2));

    }

    [Fact]
    public async Task SubscribeAsync_WhenHandlerFails_RedeliversAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var pubSub = new PubSub(transport);
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
        }, new PubSubSubscriptionOptions { Subscription = "retry-subscription", MaxAttempts = 2 }, cts.Token);

        await pubSub.PublishAsync(new PreviewEvent { Data = "retry" }, cancellationToken: cancellationToken);

        await received.WaitAsync(TimeSpan.FromSeconds(2));
        var stats = await transport.GetStatsAsync("retry-subscription", cancellationToken);
        Assert.Equal(1, stats.Completed);
        Assert.Equal(1, stats.Abandoned);
    }


    [Fact]
    public async Task SubscribeAsync_WithSameKeyAndSameRegistration_ReturnsExistingSubscriptionAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var pubSub = new PubSub(new InMemoryMessageTransport());
        Func<IReceivedMessage<PreviewEvent>, CancellationToken, Task> handler = (_, _) => Task.CompletedTask;

        await using var first = await pubSub.SubscribeAsync(handler, new PubSubSubscriptionOptions { Subscription = "same-key", Key = "shared" }, cancellationToken);
        var second = await pubSub.SubscribeAsync(handler, new PubSubSubscriptionOptions { Subscription = "same-key", Key = "shared" }, cancellationToken);

        Assert.Same(first, second);
    }

    [Fact]
    public async Task SubscribeAsync_WithSameKeyAndDifferentHandler_ThrowsAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var pubSub = new PubSub(new InMemoryMessageTransport());

        await using var first = await pubSub.SubscribeAsync<PreviewEvent>((_, _) => Task.CompletedTask, new PubSubSubscriptionOptions { Subscription = "same-key", Key = "shared" }, cancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await pubSub.SubscribeAsync<PreviewEvent>((_, _) => Task.CompletedTask, new PubSubSubscriptionOptions { Subscription = "same-key", Key = "shared" }, cancellationToken));
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
        await using var pubSub = new PubSub(transport, new PubSubOptions { Router = new DefaultMessageRouter(routing) });
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
        }, new PubSubSubscriptionOptions { RouteType = typeof(IGroupedEvent) }, cts.Token);

        await pubSub.PublishBatchAsync(new object[]
        {
            new PreviewEvent { Data = "one" },
            new OtherEvent { Data = "two" }
        }, cancellationToken: cancellationToken);

        await received.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("order-events", subscription.Topic);
        Assert.Equal("billing-service", subscription.Subscription);
        Assert.Contains(typeof(PreviewEvent).FullName!, messageTypes);
        Assert.Contains(typeof(OtherEvent).FullName!, messageTypes);

        var stats = await transport.GetStatsAsync("billing-service", cancellationToken);
        Assert.Equal(2, stats.Completed);
    }


    private static JobScheduleProcessor CreateDispatchProcessor(IJobRuntimeStore store, IMessageTransport transport)
    {
        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var worker = new JobWorker(store, serviceProvider, nodeId: "node-a");
        return new JobScheduleProcessor(new InMemoryJobScheduler(), store, worker, nodeId: "node-a", transport: transport);
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

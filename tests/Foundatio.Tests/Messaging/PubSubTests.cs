using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
using Foundatio.Messaging;
using Foundatio.Queues;
using Foundatio.Tests.Extensions;
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

        var first = pubSub.SubscribeAsync<PreviewEvent>((message, _) =>
        {
            Assert.Equal("published", message.Message.Data);
            firstReceived.Signal();
            return Task.CompletedTask;
        }, new SubscriptionOptions { Subscription = "subscriber-a" }, cts.Token);

        var second = pubSub.SubscribeAsync<PreviewEvent>((message, _) =>
        {
            Assert.Equal("published", message.Message.Data);
            secondReceived.Signal();
            return Task.CompletedTask;
        }, new SubscriptionOptions { Subscription = "subscriber-b" }, cts.Token);

        await pubSub.PublishAsync(new PreviewEvent { Data = "published" }, cancellationToken: cancellationToken);

        await firstReceived.WaitAsync(TimeSpan.FromSeconds(2));
        await secondReceived.WaitAsync(TimeSpan.FromSeconds(2));
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await second);

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

        var subscription = pubSub.SubscribeAsync<PreviewEvent>((message, _) =>
        {
            Assert.StartsWith("batch-", message.Message.Data);
            received.Signal();
            return Task.CompletedTask;
        }, new SubscriptionOptions { Subscription = "batch-subscription" }, cts.Token);

        await pubSub.PublishBatchAsync([
            new PreviewEvent { Data = "batch-one" },
            new PreviewEvent { Data = "batch-two" }
        ], cancellationToken: cancellationToken);

        await received.WaitAsync(TimeSpan.FromSeconds(2));
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await subscription);

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

        var subscription = pubSub.SubscribeAsync<PreviewEvent>((message, _) =>
        {
            received.TrySetResult(message);
            return Task.CompletedTask;
        }, new SubscriptionOptions { Subscription = "metadata-subscription" }, cts.Token);

        await pubSub.PublishAsync(new PreviewEvent { Data = "metadata" }, new PublishOptions
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

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await subscription);
    }

    [Fact]
    public async Task PublishAsync_WithDelay_DelaysDeliveryAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var pubSub = new PubSub(new InMemoryMessageTransport());
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var received = new AsyncCountdownEvent(1);

        var subscription = pubSub.SubscribeAsync<PreviewEvent>((_, _) =>
        {
            received.Signal();
            return Task.CompletedTask;
        }, new SubscriptionOptions { Subscription = "delayed-subscription" }, cts.Token);

        await pubSub.PublishAsync(new PreviewEvent { Data = "later" }, new PublishOptions { Delay = TimeSpan.FromMilliseconds(250) }, cancellationToken);

        await Assert.ThrowsAsync<TimeoutException>(async () => await received.WaitAsync(TimeSpan.FromMilliseconds(50)));
        await received.WaitAsync(TimeSpan.FromSeconds(2));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await subscription);
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

        var subscription = pubSub.SubscribeAsync<PreviewEvent>((message, _) =>
        {
            attempts++;
            Assert.Equal(attempts, message.Attempts);
            received.Signal();

            if (attempts == 1)
                throw new InvalidOperationException("try again");

            return Task.CompletedTask;
        }, new SubscriptionOptions { Subscription = "retry-subscription", MaxAttempts = 2 }, cts.Token);

        await pubSub.PublishAsync(new PreviewEvent { Data = "retry" }, cancellationToken: cancellationToken);

        await received.WaitAsync(TimeSpan.FromSeconds(2));
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await subscription);

        var stats = await transport.GetStatsAsync("retry-subscription", cancellationToken);
        Assert.Equal(1, stats.Completed);
        Assert.Equal(1, stats.Abandoned);
    }

    private sealed class PreviewEvent
    {
        public string? Data { get; set; }
    }
}

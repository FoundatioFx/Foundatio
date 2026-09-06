using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Messaging;
using Moq;
using Xunit;

namespace Foundatio.Tests.Messaging;

public class RecoveryBehaviorTests
{
    [Fact]
    public async Task HybridCache_SubscriptionGap_WaitsAndDiscardsStaleLocalData()
    {
        var token = TestContext.Current.CancellationToken;
        using var distributed = new InMemoryCacheClient();
        var subscription = new Mock<IMessageSubscription>();
        long version = 0;
        Task ready = Task.CompletedTask;
        subscription.SetupGet(s => s.RecoveryVersion).Returns(() => version);
        subscription.Setup(s => s.WaitUntilReadyAsync(It.IsAny<CancellationToken>())).Returns((CancellationToken ct) => ready.WaitAsync(ct));
        var bus = new Mock<IMessageBus>();
        bus.SetupGet(b => b.SupportsTemporarySubscriptions).Returns(true);
        bus.Setup(b => b.SubscribeAsync(It.IsAny<Func<IMessageContext<HybridCacheClient.InvalidateCache>, CancellationToken, Task>>(), It.IsAny<MessageSubscriptionOptions>(), It.IsAny<CancellationToken>())).ReturnsAsync(subscription.Object);
        using var cache = new HybridCacheClient(distributed, bus.Object);
        await distributed.SetAsync("key", "old");
        Assert.Equal("old", (await cache.GetAsync<string>("key")).Value);
        await distributed.SetAsync("key", "new");
        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ready = restored.Task;
        version++;
        var read = cache.GetAsync<string>("key");
        Assert.False(read.IsCompleted);
        restored.SetResult();
        Assert.Equal("new", (await read.WaitAsync(TimeSpan.FromSeconds(5), token)).Value);
    }

    [Fact]
    public async Task DirectReceive_CancelledBeforeDisposal_ReturnsWorkImmediately()
    {
        var token = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var bus = new MessageBus(transport, new() { OwnsTransport = false });
        await bus.SendAsync(new Event(), cancellationToken: token);
        using var receiving = CancellationTokenSource.CreateLinkedTokenSource(token);
        var delivery = await bus.ReceiveAsync<Event>(cancellationToken: receiving.Token);
        Assert.NotNull(delivery);
        await receiving.CancelAsync();
        await delivery.DisposeAsync();
        await using var redelivery = await bus.ReceiveAsync<Event>(cancellationToken: token);
        Assert.NotNull(redelivery);
    }

    public sealed record Event;
}

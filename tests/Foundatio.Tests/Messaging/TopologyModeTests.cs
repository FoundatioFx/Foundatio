using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
using Foundatio.Messaging;
using Foundatio.Tests.Extensions;
using Xunit;

namespace Foundatio.Tests.Messaging;

public class TopologyModeTests
{
    [Fact]
    public async Task Validate_WithPreProvisionedTopology_DeliversAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        // Provision out of band (the admin path), then run the bus in validate-only mode.
        var topic = DestinationAddress.ForTopic("topology-event");
        var subscription = DestinationAddress.ForSubscription("topology-event", "svc");
        await transport.EnsureAsync([new DestinationDeclaration { Address = topic }, new DestinationDeclaration { Address = subscription }], cancellationToken);

        await using var bus = new MessageBus(transport, new MessageBusOptions { Topology = TopologyMode.Validate, OwnsTransport = false });
        var received = new AsyncCountdownEvent(1);
        await using var handle = await bus.SubscribeAsync<TopologyEvent>((message, _) =>
        {
            Assert.Equal("hello", message.Message.Data);
            received.Signal();
            return Task.CompletedTask;
        }, new MessageSubscriptionOptions { Subscription = "svc" }, cts.Token);

        await bus.PublishAsync(new TopologyEvent { Data = "hello" }, cancellationToken: cancellationToken);
        await received.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Validate_WithMissingTopology_ThrowsAndCreatesNothingAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var bus = new MessageBus(transport, new MessageBusOptions { Topology = TopologyMode.Validate, OwnsTransport = false });

        await Assert.ThrowsAsync<MessageBusException>(() => bus.PublishAsync(new TopologyEvent { Data = "hello" }, cancellationToken: cancellationToken));
        await Assert.ThrowsAsync<MessageBusException>(() => bus.SubscribeAsync<TopologyEvent>((_, _) => Task.CompletedTask, cancellationToken: cancellationToken));

        Assert.False(await transport.ExistsAsync(DestinationAddress.ForTopic("topology-event"), cancellationToken));
    }

    [Fact]
    public async Task None_NeverTouchesTopologyAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var bus = new MessageBus(transport, new MessageBusOptions { Topology = TopologyMode.None, OwnsTransport = false });

        // Publishing to a topic that was never provisioned must not create it (real pub/sub drop semantics).
        await bus.PublishAsync(new TopologyEvent { Data = "dropped" }, cancellationToken: cancellationToken);
        Assert.False(await transport.ExistsAsync(DestinationAddress.ForTopic("topology-event"), cancellationToken));
    }

    [Fact]
    public async Task None_WithPreProvisionedTopology_DeliversAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        var topic = DestinationAddress.ForTopic("topology-event");
        var subscription = DestinationAddress.ForSubscription("topology-event", "svc");
        await transport.EnsureAsync([new DestinationDeclaration { Address = topic }, new DestinationDeclaration { Address = subscription }], cancellationToken);

        await using var bus = new MessageBus(transport, new MessageBusOptions { Topology = TopologyMode.None, OwnsTransport = false });
        var received = new AsyncCountdownEvent(1);
        await using var handle = await bus.SubscribeAsync<TopologyEvent>((message, _) =>
        {
            received.Signal();
            return Task.CompletedTask;
        }, new MessageSubscriptionOptions { Subscription = "svc" }, cts.Token);

        await bus.PublishAsync(new TopologyEvent { Data = "hello" }, cancellationToken: cancellationToken);
        await received.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [MessageRoute("topology-event")]
    private sealed class TopologyEvent
    {
        public string? Data { get; set; }
    }
}

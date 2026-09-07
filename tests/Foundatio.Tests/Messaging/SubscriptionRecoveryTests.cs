using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Foundatio.Tests.Messaging;

public class SubscriptionRecoveryTests
{
    [Fact]
    public async Task ConcurrentReceives_DestinationDisappears_CancelsSiblingBeforeReprovisioning()
    {
        var token = TestContext.Current.CancellationToken;
        var transport = new Mock<IMessageTransport>();
        transport.As<ITransportInfo>().SetupGet(t => t.SupportedRoles).Returns(new HashSet<DestinationRole> { DestinationRole.Queue });
        transport.As<ITransportInfo>().Setup(t => t.GetCapabilities(It.IsAny<DestinationAddress>()))
            .Returns(new TransportCapabilities { MaxReceiveBatchSize = 1, MaxConcurrentReceives = 2 });
        var siblingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var siblingCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int receives = 0;
        int provisions = 0;
        transport.As<ISupportsProvisioning>().Setup(t => t.EnsureAsync(It.IsAny<IReadOnlyList<DestinationDeclaration>>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (Interlocked.Increment(ref provisions) > 1)
                {
                    Assert.True(siblingCancelled.Task.IsCompleted);
                    recovered.TrySetResult();
                }
                return Task.CompletedTask;
            });
        transport.As<ISupportsPull>().Setup(t => t.ReceiveAsync(It.IsAny<DestinationAddress>(), It.IsAny<ReceiveRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (DestinationAddress source, ReceiveRequest _, CancellationToken ct) =>
            {
                int call = Interlocked.Increment(ref receives);
                if (call == 1)
                {
                    await siblingStarted.Task.WaitAsync(ct);
                    throw new MessageDestinationNotFoundException(source, new InvalidOperationException("Queue deleted"));
                }
                if (call == 2)
                {
                    siblingStarted.TrySetResult();
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                    finally { siblingCancelled.TrySetResult(); }
                }
                return (IReadOnlyList<TransportEntry>)Array.Empty<TransportEntry>();
            });
        await using var bus = new MessageBus(transport.Object);
        await using var subscription = await bus.ConsumeAsync((_, _) => Task.CompletedTask,
            new MessageConsumerOptions { Destination = "work", MaxConcurrency = 2 }, token);
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        Assert.True(subscription.RecoveryVersion > 0);
    }

    [Fact]
    public async Task NamedSubscription_DeletedWhileListening_RebindsAndSignalsGap()
    {
        var token = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var bus = new MessageBus(transport, new() { OwnsTransport = false });
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await bus.SubscribeAsync<Event>((_, _) => { received.TrySetResult(); return Task.CompletedTask; }, new() { Topic = "events", Subscription = "audit" }, token);
        await subscription.WaitUntilReadyAsync(token);
        await transport.DeleteAsync(subscription.Source, token);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (subscription.RecoveryVersion == 0)
            await Task.Delay(10, timeout.Token);
        await subscription.WaitUntilReadyAsync(timeout.Token);
        await bus.PublishAsync(new Event(), new() { Topic = "events" }, token);

        await received.Task.WaitAsync(TimeSpan.FromSeconds(2), token);
    }

    [Fact]
    public async Task TemporarySubscription_TransientRenewalFailure_RetriesWithinLease()
    {
        var token = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var transport = new Mock<IMessageTransport>();
        transport.As<ITransportInfo>().SetupGet(t => t.SupportedRoles).Returns(new HashSet<DestinationRole> { DestinationRole.Topic, DestinationRole.Subscription });
        transport.As<ITransportInfo>().Setup(t => t.GetCapabilities(It.IsAny<DestinationAddress>())).Returns(new TransportCapabilities());
        transport.As<ISupportsPull>().Setup(t => t.ReceiveAsync(It.IsAny<DestinationAddress>(), It.IsAny<ReceiveRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<TransportEntry>());
        transport.As<ISupportsProvisioning>().Setup(t => t.EnsureAsync(It.IsAny<IReadOnlyList<DestinationDeclaration>>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int renewals = 0;
        transport.As<ISupportsEphemeralSubscriptions>().Setup(t => t.RenewSubscriptionAsync(It.IsAny<DestinationAddress>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (Interlocked.Increment(ref renewals) == 1)
                    throw new InvalidOperationException("Temporary outage");
                retried.TrySetResult();
                return Task.FromResult(true);
            });
        await using var bus = new MessageBus(transport.Object, new MessageBusOptions { TimeProvider = time, OwnsTransport = false });
        await using var subscription = await bus.SubscribeAsync<Event>((_, _) => Task.CompletedTask, cancellationToken: token);
        await Task.Delay(30, token);
        time.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(30, token);
        time.Advance(TimeSpan.FromSeconds(2));
        await retried.Task.WaitAsync(TimeSpan.FromSeconds(2), token);
    }

    [Fact]
    public async Task TemporarySubscription_LostLease_RecreatesAndSignalsGap()
    {
        var token = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var transport = new Mock<IMessageTransport>();
        transport.As<ITransportInfo>().SetupGet(t => t.SupportedRoles).Returns(new HashSet<DestinationRole> { DestinationRole.Topic, DestinationRole.Subscription });
        transport.As<ITransportInfo>().Setup(t => t.GetCapabilities(It.IsAny<DestinationAddress>())).Returns(new TransportCapabilities());
        transport.As<ISupportsPull>().Setup(t => t.ReceiveAsync(It.IsAny<DestinationAddress>(), It.IsAny<ReceiveRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<TransportEntry>());
        int declarations = 0;
        transport.As<ISupportsProvisioning>().Setup(t => t.EnsureAsync(It.IsAny<IReadOnlyList<DestinationDeclaration>>(), It.IsAny<CancellationToken>()))
            .Callback(() => Interlocked.Increment(ref declarations)).Returns(Task.CompletedTask);
        transport.As<ISupportsEphemeralSubscriptions>().Setup(t => t.RenewSubscriptionAsync(It.IsAny<DestinationAddress>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        await using var bus = new MessageBus(transport.Object, new MessageBusOptions { TimeProvider = time, OwnsTransport = false });
        await using var subscription = await bus.SubscribeAsync<Event>((_, _) => Task.CompletedTask, cancellationToken: token);
        await subscription.WaitUntilReadyAsync(token);
        int initialDeclarations = Volatile.Read(ref declarations);
        for (int i = 0; i < 40 && Volatile.Read(ref declarations) == initialDeclarations; i++)
        {
            time.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(10, token);
        }
        Assert.True(Volatile.Read(ref declarations) > initialDeclarations);
        await subscription.WaitUntilReadyAsync(token);
        Assert.True(subscription.RecoveryVersion > 0);
    }

    [Fact]
    public void UnmatchedMessage_DefaultRetry_AllowsRollingDeployment()
    {
        var policy = new RetryPolicy();
        Assert.NotNull(policy.UnmatchedBackoff);
        Assert.InRange(policy.UnmatchedBackoff(1), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(6));
    }

    public sealed record Event;
}

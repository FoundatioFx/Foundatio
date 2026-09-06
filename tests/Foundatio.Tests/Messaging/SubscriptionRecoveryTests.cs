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

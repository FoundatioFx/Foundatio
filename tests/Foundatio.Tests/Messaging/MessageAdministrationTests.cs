using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Foundatio.Tests.Messaging;

public class MessageAdministrationTests
{
    [Fact]
    public async Task DeadLetterAsync_SubscriptionFallback_UsesSameDestinationForAdministration()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        await using var backing = new InMemoryMessageTransport();
        var transport = new Mock<ISupportsPull>();
        transport.Setup(t => t.SendAsync(It.IsAny<DestinationAddress>(), It.IsAny<IReadOnlyList<TransportMessage>>(), It.IsAny<TransportSendOptions>(), It.IsAny<CancellationToken>()))
            .Returns((DestinationAddress address, IReadOnlyList<TransportMessage> messages, TransportSendOptions options, CancellationToken ct) => backing.SendAsync(address, messages, options, ct));
        transport.Setup(t => t.ReceiveAsync(It.IsAny<DestinationAddress>(), It.IsAny<ReceiveRequest>(), It.IsAny<CancellationToken>()))
            .Returns((DestinationAddress address, ReceiveRequest request, CancellationToken ct) => backing.ReceiveAsync(address, request, ct));
        transport.Setup(t => t.CompleteAsync(It.IsAny<TransportEntry>(), It.IsAny<CancellationToken>()))
            .Returns((TransportEntry entry, CancellationToken ct) => backing.CompleteAsync(entry, ct));
        transport.Setup(t => t.AbandonAsync(It.IsAny<TransportEntry>(), It.IsAny<CancellationToken>()))
            .Returns((TransportEntry entry, CancellationToken ct) => backing.AbandonAsync(entry, ct));
        transport.As<ISupportsStats>().Setup(t => t.GetStatsAsync(It.IsAny<DestinationAddress>(), It.IsAny<CancellationToken>()))
            .Returns((DestinationAddress address, CancellationToken ct) => backing.GetStatsAsync(address, ct));
        transport.As<ISupportsProvisioning>().Setup(t => t.EnsureAsync(It.IsAny<IReadOnlyList<DestinationDeclaration>>(), It.IsAny<CancellationToken>()))
            .Returns((IReadOnlyList<DestinationDeclaration> declarations, CancellationToken ct) => backing.EnsureAsync(declarations, ct));
        transport.As<ISupportsProvisioning>().Setup(t => t.ExistsAsync(It.IsAny<DestinationAddress>(), It.IsAny<CancellationToken>()))
            .Returns((DestinationAddress address, CancellationToken ct) => backing.ExistsAsync(address, ct));
        var source = DestinationAddress.ForSubscription("events", "audit");
        await backing.EnsureAsync([new() { Address = source }], token);
        await backing.SendAsync(source, [new() { MessageId = "delete", Body = new byte[] { 1 } }, new() { MessageId = "replay", Body = new byte[] { 2 } }], new(), token);
        var entries = await backing.ReceiveAsync(source, new() { MaxMessages = 2 }, token);
        var administration = new MessageAdministration(transport.Object);

        // Act
        foreach (var entry in entries)
            await MessageContext.DeadLetterAsync(transport.Object, entry, "failure", null, NullLogger.Instance, token);

        // Assert
        Assert.Equal(2, (await administration.GetStatsAsync(source, token)).Deadletter);
        var dead = await administration.PeekDeadLettersAsync(source, 2, token);
        Assert.Equal(2, dead.Count);
        Assert.True(await administration.DeleteDeadLetterAsync(source, dead[0].Id, token));
        Assert.True(await administration.ReplayDeadLetterAsync(source, dead[1].Id, cancellationToken: token));
        Assert.Equal(0, (await administration.GetStatsAsync(source, token)).Deadletter);
        var replayed = Assert.Single(await backing.ReceiveAsync(source, new(), token));
        Assert.Equal(dead[1].ApplicationMessageId, replayed.ApplicationMessageId);
        await backing.CompleteAsync(replayed, token);
    }
}

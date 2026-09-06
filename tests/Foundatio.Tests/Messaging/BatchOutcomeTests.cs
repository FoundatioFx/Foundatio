using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Moq;
using Xunit;

namespace Foundatio.Tests.Messaging;

public class BatchOutcomeTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void EnsureAccepted_InvalidInputIndex_RejectsResult(int index)
    {
        var result = new SendResult { Items = [new SendItemResult { Index = index }] };
        Assert.Throws<MessageBusException>(() => result.EnsureAccepted(1));
    }

    [Fact]
    public async Task SendBatchAsync_UnorderedPartialResults_PreservesEveryInputOutcome()
    {
        var transport = new Mock<IMessageTransport>();
        transport.As<ITransportInfo>().SetupGet(t => t.SupportedRoles).Returns(new HashSet<DestinationRole> { DestinationRole.Queue });
        transport.As<ITransportInfo>().Setup(t => t.GetCapabilities(It.IsAny<DestinationAddress>())).Returns(new TransportCapabilities { MaxBatchSize = 2 });
        transport.Setup(t => t.SendAsync(It.IsAny<DestinationAddress>(), It.IsAny<IReadOnlyList<TransportMessage>>(), It.IsAny<TransportSendOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendResult { Items = [new SendItemResult { Index = 1, Status = MessageSendStatus.Accepted, MessageId = "broker-b" }, new SendItemResult { Index = 0, Status = MessageSendStatus.Rejected, ErrorCode = "Throttled", Retryable = true }] });
        await using var bus = new MessageBus(transport.Object, new MessageBusOptions { OwnsTransport = false });
        var failure = await Assert.ThrowsAsync<MessageSendException>(() => bus.SendBatchAsync<Event>([
            new MessageBatchItem<Event>(new Event(), "a"), new MessageBatchItem<Event>(new Event(), "b"), new MessageBatchItem<Event>(new Event(), "c")
        ], cancellationToken: TestContext.Current.CancellationToken));
        Assert.Collection(failure.Outcomes,
            a => { Assert.Equal("a", a.MessageId); Assert.Equal(MessageSendStatus.Rejected, a.Status); Assert.True(a.Retryable); Assert.Equal("Throttled", a.ErrorCode); },
            b => { Assert.Equal("b", b.MessageId); Assert.Equal(MessageSendStatus.Accepted, b.Status); },
            c => { Assert.Equal("c", c.MessageId); Assert.Equal(MessageSendStatus.NotAttempted, c.Status); });
    }

    public sealed record Event;
}

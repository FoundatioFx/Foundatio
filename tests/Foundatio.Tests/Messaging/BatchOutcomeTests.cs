using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Foundatio.Tests.Messaging;

public class BatchOutcomeTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SendAsync_OversizedScheduledMessage_RejectsBeforePersisting(bool publish, bool deliverAt)
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var store = new InMemoryJobRuntimeStore(time);
        var transport = CreateSizeLimitedTransport();
        await using var bus = new MessageBus(transport.Object, new MessageBusOptions { RuntimeStore = store, TimeProvider = time });

        // Act
        Func<Task<string>> send = publish
            ? () => bus.PublishAsync(new SizedEvent(new string('x', 100)), new MessagePublishOptions { Delay = deliverAt ? null : TimeSpan.FromMinutes(1), DeliverAt = deliverAt ? time.GetUtcNow().AddMinutes(1) : null }, token)
            : () => bus.SendAsync(new SizedEvent(new string('x', 100)), new MessageSendOptions { Delay = deliverAt ? null : TimeSpan.FromMinutes(1), DeliverAt = deliverAt ? time.GetUtcNow().AddMinutes(1) : null }, token);
        var failure = await Assert.ThrowsAsync<MessageBusException>(send);

        // Assert
        Assert.Contains("maximum of 32 bytes", failure.Message);
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Empty(await store.ClaimDueDispatchesAsync(time.GetUtcNow(), 10, "test", TimeSpan.FromMinutes(1), token));
        transport.Verify(t => t.SendAsync(It.IsAny<DestinationAddress>(), It.IsAny<IReadOnlyList<TransportMessage>>(), It.IsAny<TransportSendOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SendBatchAsync_OversizedScheduledMessage_RejectsBeforePersisting(bool publish, bool deliverAt)
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var store = new InMemoryJobRuntimeStore(time);
        var transport = CreateSizeLimitedTransport();
        await using var bus = new MessageBus(transport.Object, new MessageBusOptions { RuntimeStore = store, TimeProvider = time });
        var messages = new[] { new SizedEvent("small"), new SizedEvent(new string('x', 100)) };

        // Act
        Func<Task<IReadOnlyList<string>>> send = publish
            ? () => bus.PublishBatchAsync(messages, new MessagePublishOptions { Delay = deliverAt ? null : TimeSpan.FromMinutes(1), DeliverAt = deliverAt ? time.GetUtcNow().AddMinutes(1) : null }, token)
            : () => bus.SendBatchAsync(messages, new MessageSendOptions { Delay = deliverAt ? null : TimeSpan.FromMinutes(1), DeliverAt = deliverAt ? time.GetUtcNow().AddMinutes(1) : null }, token);
        var failure = await Assert.ThrowsAsync<MessageSendException>(send);

        // Assert
        Assert.Contains("maximum of 32 bytes", Assert.IsType<MessageBusException>(failure.InnerException).Message);
        Assert.All(failure.Outcomes, outcome => Assert.Equal(MessageSendStatus.NotAttempted, outcome.Status));
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Empty(await store.ClaimDueDispatchesAsync(time.GetUtcNow(), 10, "test", TimeSpan.FromMinutes(1), token));
        transport.Verify(t => t.SendAsync(It.IsAny<DestinationAddress>(), It.IsAny<IReadOnlyList<TransportMessage>>(), It.IsAny<TransportSendOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(MessageSendStatus.Rejected, false)]
    [InlineData(MessageSendStatus.Unknown, false)]
    [InlineData(MessageSendStatus.NotAttempted, false)]
    [InlineData(MessageSendStatus.Rejected, true)]
    [InlineData(MessageSendStatus.Unknown, true)]
    public async Task SendAsync_PartialAcceptance_PreservesTheApplicationIdAndProviderOutcome(MessageSendStatus status, bool throws)
    {
        var transport = new Mock<IMessageTransport>();
        var item = new SendItemResult { Index = 0, MessageId = "broker-id", Status = status, ErrorCode = "Unavailable", ErrorMessage = "Retry later", Retryable = true };
        var send = transport.Setup(t => t.SendAsync(It.IsAny<DestinationAddress>(), It.IsAny<IReadOnlyList<TransportMessage>>(), It.IsAny<TransportSendOptions>(), It.IsAny<CancellationToken>()));
        if (throws) send.ThrowsAsync(new TransportSendException([item], new TimeoutException()));
        else send.ReturnsAsync(new SendResult { Items = [item] });
        await using var bus = new MessageBus(transport.Object);
        var failure = await Assert.ThrowsAsync<MessageSendException>(() => bus.SendAsync(new Event(), new MessageSendOptions { MessageId = "application-id" }, TestContext.Current.CancellationToken));
        var outcome = Assert.Single(failure.Outcomes);
        Assert.Equal("application-id", outcome.MessageId);
        Assert.Equal(status, outcome.Status);
        Assert.Equal("Unavailable", outcome.ErrorCode);
        Assert.Equal("Retry later", outcome.ErrorMessage);
        Assert.True(outcome.Retryable);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task SendAsync_InvalidProviderIndex_DoesNotReportAcceptance(int index)
    {
        var transport = new Mock<IMessageTransport>();
        transport.Setup(t => t.SendAsync(It.IsAny<DestinationAddress>(), It.IsAny<IReadOnlyList<TransportMessage>>(), It.IsAny<TransportSendOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendResult { Items = [new SendItemResult { Index = index, Status = MessageSendStatus.Accepted }] });
        await using var bus = new MessageBus(transport.Object);
        var failure = await Assert.ThrowsAsync<MessageSendException>(() => bus.SendAsync(new Event(), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(MessageSendStatus.Unknown, Assert.Single(failure.Outcomes).Status);
    }

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

    private static Mock<IMessageTransport> CreateSizeLimitedTransport()
    {
        var transport = new Mock<IMessageTransport>();
        transport.As<ITransportInfo>().SetupGet(t => t.SupportedRoles).Returns(new HashSet<DestinationRole> { DestinationRole.Queue, DestinationRole.Topic });
        transport.As<ITransportInfo>().Setup(t => t.GetCapabilities(It.IsAny<DestinationAddress>())).Returns(new TransportCapabilities { MaxMessageBytes = 32 });
        return transport;
    }

    public sealed record Event;
    private sealed record SizedEvent(string Data);
}

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Xunit;

namespace Foundatio.Tests.Messaging;

public class InMemoryMessageTransportTests : MessageTransportConformanceTests
{
    public InMemoryMessageTransportTests(ITestOutputHelper output) : base(output) { }

    protected override IMessageTransport CreateTransport()
    {
        return new InMemoryMessageTransport();
    }

    [Fact]
    public void MessageHeaders_AreImmutableAndCaseInsensitive()
    {
        var source = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["message.type"] = "order.created"
        };

        var headers = MessageHeaders.Create(source);
        source["message.type"] = "changed";

        Assert.Equal("order.created", headers["MESSAGE.TYPE"]);
        Assert.Equal("order.created", headers.GetValueOrDefault("Message.Type"));
        Assert.True(headers.ContainsKey("MESSAGE.TYPE"));

        var updated = headers.ToBuilder()
            .Set("TraceParent", "00-123")
            .SetIfMissing("traceparent", "ignored")
            .Build();

        Assert.Equal("00-123", updated["traceparent"]);
        Assert.False(headers.ContainsKey("traceparent"));
    }

    [Fact]
    public override Task CanSendAndReceiveBatchAsync()
    {
        return base.CanSendAndReceiveBatchAsync();
    }

    [Fact]
    public override Task AbandonAsync_RedeliversWithIncrementedDeliveryCountAsync()
    {
        return base.AbandonAsync_RedeliversWithIncrementedDeliveryCountAsync();
    }

    [Fact]
    public override Task CompleteAsync_WithExpiredReceipt_ThrowsReceiptExpiredExceptionAsync()
    {
        return base.CompleteAsync_WithExpiredReceipt_ThrowsReceiptExpiredExceptionAsync();
    }

    [Fact]
    public override Task SubscribeAsync_DeliversPushMessagesAsync()
    {
        return base.SubscribeAsync_DeliversPushMessagesAsync();
    }

    [Fact]
    public override Task SendAsync_ToTopic_FansOutToSubscriptionsAsync()
    {
        return base.SendAsync_ToTopic_FansOutToSubscriptionsAsync();
    }

    [Fact]
    public override Task ReceiveAsync_RespectsPriorityAsync()
    {
        return base.ReceiveAsync_RespectsPriorityAsync();
    }

    [Fact]
    public override Task SendAsync_WithDeliverAt_DelaysVisibilityAsync()
    {
        return base.SendAsync_WithDeliverAt_DelaysVisibilityAsync();
    }

    [Fact]
    public override Task DeadLetterAsync_MovesEntryToDeadletterStatsAsync()
    {
        return base.DeadLetterAsync_MovesEntryToDeadletterStatsAsync();
    }

    [Fact]
    public override Task ReceiveAsync_WithExpiredMessage_DeadlettersAndSkipsAsync()
    {
        return base.ReceiveAsync_WithExpiredMessage_DeadlettersAndSkipsAsync();
    }

    [Fact]
    public override Task ReceiveAsync_AfterVisibilityTimeout_RedeliversAsync()
    {
        return base.ReceiveAsync_AfterVisibilityTimeout_RedeliversAsync();
    }

    [Fact]
    public override Task CompetingConsumers_DoNotReceiveTheSameInFlightMessageAsync()
    {
        return base.CompetingConsumers_DoNotReceiveTheSameInFlightMessageAsync();
    }

    [Fact]
    public override Task ReceiveDeadLetteredAsync_ReturnsPoisonPayloadAndReasonAsync()
    {
        return base.ReceiveDeadLetteredAsync_ReturnsPoisonPayloadAndReasonAsync();
    }
}

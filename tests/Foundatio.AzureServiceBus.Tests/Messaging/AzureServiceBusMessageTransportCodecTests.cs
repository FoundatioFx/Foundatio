using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Azure.Messaging.ServiceBus;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Xunit;

namespace Foundatio.AzureServiceBus.Tests.Messaging;

public class AzureServiceBusMessageTransportCodecTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void EncodeThenDecode_RoundTripsIdBodyHeadersAndGroupKey(bool sessions, bool deduplication)
    {
        // Arrange
        var message = new OutgoingMessage
        {
            MessageId = "id with spaces ✓",
            Body = Encoding.UTF8.GetBytes("payload"),
            Headers = new Dictionary<string, string> { ["message-type"] = "order.placed", ["empty"] = "", ["unicode"] = "héllo ✓" },
            GroupKey = "order-42",
            DeduplicationId = "dedupe-42"
        };

        // Act
        var encoded = AzureServiceBusMessageTransportCodec.Encode(message, new AzureServiceBusEncodeOptions(Now, sessions, deduplication, null));
        var decoded = AzureServiceBusMessageTransportCodec.Decode(Receive(encoded), deadLetter: false);

        // Assert
        Assert.Equal(message.MessageId, decoded.MessageId);
        Assert.Equal("payload", Encoding.UTF8.GetString(decoded.Body.Span));
        Assert.Equal(message.Headers, decoded.Headers);
        Assert.Equal("order-42", decoded.GroupKey);
        Assert.Equal("order-42", encoded.SessionId);
        Assert.Equal(deduplication ? "dedupe-42" : message.MessageId, encoded.MessageId);
        Assert.Equal(1, decoded.DeliveryCount);
    }

    [Fact]
    public void Encode_UngroupedMessageInSessionMode_SharesAnUngroupedSessionAndDecodesWithoutAGroupKey()
    {
        var encoded = AzureServiceBusMessageTransportCodec.Encode(Message(), new AzureServiceBusEncodeOptions(Now, Sessions: true, Deduplication: false, null));

        Assert.StartsWith(AzureServiceBusMessageTransportCodec.UngroupedSessionPrefix, encoded.SessionId);
        Assert.Null(AzureServiceBusMessageTransportCodec.Decode(Receive(encoded), deadLetter: false).GroupKey);
    }

    [Fact]
    public void Encode_HeadersOverTheApplicationPropertyBudget_TravelAtTheStartOfTheBody()
    {
        // Arrange
        var headers = Enumerable.Range(0, 60).ToDictionary(i => $"h{i}", _ => new string('v', 1000));
        var message = Message() with { Headers = headers };

        // Act
        var encoded = AzureServiceBusMessageTransportCodec.Encode(message, new AzureServiceBusEncodeOptions(Now, false, false, null));
        var decoded = AzureServiceBusMessageTransportCodec.Decode(Receive(encoded), deadLetter: false);

        // Assert
        Assert.DoesNotContain(encoded.ApplicationProperties.Keys, k => k.StartsWith('h'));
        Assert.True(encoded.ApplicationProperties.ContainsKey(AzureServiceBusMessageTransportCodec.HeadersProperty));
        Assert.Equal(headers, decoded.Headers);
        Assert.Equal("body", Encoding.UTF8.GetString(decoded.Body.Span));
    }

    [Fact]
    public void Encode_DelayedMessage_IsScheduledUnlessItHasAGroupKeyInSessionMode()
    {
        // Arrange
        var delayed = Message() with { DeliverAt = Now.AddMinutes(5) };

        // Act
        var scheduled = AzureServiceBusMessageTransportCodec.Encode(delayed, new AzureServiceBusEncodeOptions(Now, false, false, null));
        var ungrouped = AzureServiceBusMessageTransportCodec.Encode(delayed, new AzureServiceBusEncodeOptions(Now, true, false, null));
        var grouped = AzureServiceBusMessageTransportCodec.Encode(delayed with { GroupKey = "g" }, new AzureServiceBusEncodeOptions(Now, true, false, null));

        // Assert
        Assert.InRange(scheduled.ScheduledEnqueueTime - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5));
        Assert.InRange(ungrouped.ScheduledEnqueueTime - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5));
        Assert.Equal(default, grouped.ScheduledEnqueueTime);
        Assert.Equal(Now.AddMinutes(5), AzureServiceBusMessageTransportCodec.GetDeliverAt(Receive(grouped)));
    }

    [Fact]
    public void Encode_ToOneSubscription_NamesItAndHashesTheDeduplicationIdWithIt()
    {
        var encoded = AzureServiceBusMessageTransportCodec.Encode(Message() with { DeduplicationId = "same" }, new AzureServiceBusEncodeOptions(Now, false, Deduplication: true, "billing"));

        Assert.Equal("billing", encoded.ApplicationProperties[AzureServiceBusMessageTransportCodec.TargetProperty]);
        Assert.Equal(AzureServiceBusMessageTransportNames.SubscriptionDeduplicationId("billing", "same"), encoded.MessageId);
        Assert.Equal("id-1", encoded.ApplicationProperties[AzureServiceBusMessageTransportCodec.MessageIdProperty]);
    }

    [Fact]
    public void EncodeRetry_CarriesTheDeliveryCountAndANewIdUnderDuplicateDetection()
    {
        // Arrange
        var original = Receive(AzureServiceBusMessageTransportCodec.Encode(Message() with { DeduplicationId = "same" }, new AzureServiceBusEncodeOptions(Now, false, true, null)), deliveryCount: 2);

        // Act
        var retry = AzureServiceBusMessageTransportCodec.EncodeRetry(original, deliveryCount: 2, Now.AddMinutes(1), "billing", deduplication: true);
        var decoded = AzureServiceBusMessageTransportCodec.Decode(Receive(retry), deadLetter: false);

        // Assert
        Assert.NotEqual("same", retry.MessageId);
        Assert.Equal(Now.AddMinutes(1), retry.ScheduledEnqueueTime);
        Assert.Equal("billing", retry.ApplicationProperties[AzureServiceBusMessageTransportCodec.TargetProperty]);
        Assert.Equal("id-1", decoded.MessageId);
        Assert.Equal(3, decoded.DeliveryCount);
    }

    [Fact]
    public void Decode_DeadLetter_ReadsServiceBusReasonAndDescriptionIntoTheReasonHeader()
    {
        // Arrange
        var properties = new Dictionary<string, object>
        {
            ["message-type"] = "order.placed",
            ["DeadLetterReason"] = "MaxDeliveryCountExceeded",
            ["DeadLetterErrorDescription"] = "Message could not be consumed after 10 delivery attempts."
        };
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(BinaryData.FromString("x"), messageId: "id-1", properties: properties, deliveryCount: 10);

        // Act
        var decoded = AzureServiceBusMessageTransportCodec.Decode(message, deadLetter: true);

        // Assert
        Assert.Equal("MaxDeliveryCountExceeded: Message could not be consumed after 10 delivery attempts.", decoded.Headers[KnownHeaders.DeadLetterReason]);
        Assert.Equal(["dead-letter-reason", "message-type"], decoded.Headers.Keys.Order());
    }

    [Fact]
    public void TruncateUtf8_KeepsWholeCharactersWithinTheLimit()
    {
        string reason = new string('r', 1022) + "✓✓";

        string kept = AzureServiceBusMessageTransportCodec.TruncateUtf8(reason, 1024);

        Assert.Equal(new string('r', 1022), kept);
    }

    private static OutgoingMessage Message() => new() { MessageId = "id-1", Body = Encoding.UTF8.GetBytes("body") };

    private static ServiceBusReceivedMessage Receive(ServiceBusMessage message, int deliveryCount = 1) =>
        ServiceBusModelFactory.ServiceBusReceivedMessage(message.Body, message.MessageId, sessionId: message.SessionId,
            properties: new Dictionary<string, object>(message.ApplicationProperties), deliveryCount: deliveryCount, enqueuedTime: DateTimeOffset.UtcNow);
}

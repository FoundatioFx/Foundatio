using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Foundatio.Messaging.Transport;
using Foundatio.Xunit;
using RabbitMQ.Client;
using Xunit;

namespace Foundatio.RabbitMQ.Tests.Messaging;

public class RabbitMQMessageTransportCodecTests(ITestOutputHelper output) : TestWithLoggingBase(output)
{
    private static readonly DateTimeOffset SentAt = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Encode_WithPlainHeaders_KeepsThemInTheHeaderTableAndTheBodyAsIs()
    {
        // Arrange
        var headers = new Dictionary<string, string> { ["message-type"] = "orders.created", ["unicode"] = "héllo ✓" };

        // Act
        var (properties, body) = RabbitMQMessageTransportCodec.Encode("id-1", "payload"u8.ToArray(), headers, "group", 0, SentAt, RabbitMQMessageTransportCodec.MaxHeaderTableBytes);
        var decoded = RabbitMQMessageTransportCodec.Decode(properties, body);

        // Assert
        Assert.Equal("payload", Encoding.UTF8.GetString(body.Span));
        Assert.Equal("id-1", properties.MessageId);
        Assert.Equal("orders.created", Encoding.UTF8.GetString((byte[])properties.Headers!["message-type"]!));
        Assert.Equal(headers, decoded.Headers);
        Assert.Equal("group", decoded.GroupKey);
        Assert.Equal(1, decoded.DeliveryCount);
        Assert.Equal(SentAt, decoded.EnqueuedAt);
    }

    [Fact]
    public void Encode_WithNamesTheBrokerOrClientOwn_AliasesThemAndIgnoresTheirsOnDecode()
    {
        // Arrange
        var headers = new Dictionary<string, string> { ["x-custom"] = "1", ["traceparent"] = "app", ["CC"] = "app", [""] = "empty" };

        // Act
        var (properties, body) = RabbitMQMessageTransportCodec.Encode("id", ReadOnlyMemory<byte>.Empty, headers, null, 0, SentAt, RabbitMQMessageTransportCodec.MaxHeaderTableBytes);
        // What the broker and RabbitMQ.Client add on the way.
        properties.Headers!["traceparent"] = "client"u8.ToArray();
        properties.Headers["x-death"] = new List<object?>();
        properties.Headers["x-dotnet-pub-seq-no"] = 7L;
        var decoded = RabbitMQMessageTransportCodec.Decode(properties, body);

        // Assert
        Assert.Contains(RabbitMQMessageTransportCodec.AliasPrefix + "x-custom", properties.Headers.Keys);
        Assert.DoesNotContain("x-custom", properties.Headers.Keys);
        Assert.Equal(headers, decoded.Headers);
    }

    [Fact]
    public void Encode_WithHeadersTooLargeForTheTable_MovesThemIntoTheBody()
    {
        // Arrange
        var headers = new Dictionary<string, string> { ["large"] = new string('v', 10_000), [new string('n', 300)] = "long name", ["x-y"] = "z" };

        // Act
        var (properties, body) = RabbitMQMessageTransportCodec.Encode("id", "payload"u8.ToArray(), headers, null, 0, SentAt, headerTableLimit: 1024);
        var decoded = RabbitMQMessageTransportCodec.Decode(properties, body);

        // Assert
        Assert.DoesNotContain("large", properties.Headers!.Keys);
        Assert.True(body.Length > "payload".Length);
        Assert.Equal(headers, decoded.Headers);
        Assert.Equal("payload", Encoding.UTF8.GetString(decoded.Body.Span));
    }

    [Fact]
    public void Decode_WithBrokerAndTransportDeliveryCounts_AddsThemUp()
    {
        // Arrange
        var (properties, body) = RabbitMQMessageTransportCodec.Encode("id", ReadOnlyMemory<byte>.Empty, new Dictionary<string, string>(), null, previousDeliveries: 3, SentAt, 1024);
        properties.Headers![RabbitMQMessageTransportCodec.BrokerDeliveryCountHeader] = 2L;

        // Act
        var decoded = RabbitMQMessageTransportCodec.Decode(properties, body);

        // Assert
        Assert.Equal(6, decoded.DeliveryCount);
    }

    [Fact]
    public void Decode_FromAProducerWithAMillisecondTimestampAndHugeCounts_HasNoEnqueuedTimeAndTheLargestCount()
    {
        // Arrange
        var properties = new BasicProperties
        {
            MessageId = "foreign",
            Timestamp = new AmqpTimestamp(SentAt.ToUnixTimeMilliseconds()),
            Headers = new Dictionary<string, object?>
            {
                [RabbitMQMessageTransportCodec.DeliveriesHeader] = Int64.MaxValue,
                [RabbitMQMessageTransportCodec.BrokerDeliveryCountHeader] = Int64.MaxValue
            }
        };

        // Act
        var decoded = RabbitMQMessageTransportCodec.Decode(properties, "body"u8.ToArray());

        // Assert
        Assert.Equal("foreign", decoded.MessageId);
        Assert.Null(decoded.EnqueuedAt);
        Assert.Equal(Int32.MaxValue, decoded.DeliveryCount);
    }

    [Fact]
    public void Encode_WithAnIdLongerThanAShortString_KeepsItInAHeader()
    {
        // Arrange
        string id = String.Concat(Enumerable.Repeat("😀", 64));

        // Act
        var (properties, body) = RabbitMQMessageTransportCodec.Encode(id, ReadOnlyMemory<byte>.Empty, new Dictionary<string, string>(), null, 0, SentAt, 1024);

        // Assert
        Assert.Null(properties.MessageId);
        Assert.Equal(id, RabbitMQMessageTransportCodec.Decode(properties, body).MessageId);
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(10_000)]
    public void TruncateReason_KeepsAtMostTheLimitWithoutSplittingCharacters(int length)
    {
        // Arrange
        string reason = String.Concat(Enumerable.Repeat("é😀", length / 2));

        // Act
        string kept = RabbitMQMessageTransportCodec.TruncateReason(reason);

        // Assert
        Assert.StartsWith(kept, reason);
        Assert.InRange(Encoding.UTF8.GetByteCount(kept), RabbitMQMessageTransportCodec.MaxReasonBytes - 6, RabbitMQMessageTransportCodec.MaxReasonBytes);
        Assert.False(Char.IsHighSurrogate(kept[^1]));
    }

    [Fact]
    public void DelayLevels_RoutingKeys_SpellTheDelayInBinaryAndMatchTheLevelBindings()
    {
        // Arrange
        string key = RabbitMQDelayLevels.RoutingKey(5, "foundatio.queue.orders");
        string[] words = key.Split('.');

        // Act
        string bits = String.Concat(words.Take(RabbitMQDelayLevels.Count));

        // Assert
        Assert.Equal(Convert.ToString(5, 2).PadLeft(RabbitMQDelayLevels.Count, '0'), bits);
        Assert.EndsWith(".foundatio.queue.orders", key);
        Assert.Equal(RabbitMQDelayLevels.Count + 3, words.Length);
        Assert.Equal("1.#", RabbitMQDelayLevels.LevelBindingKey(RabbitMQDelayLevels.Count - 1, set: true));
        Assert.Equal(String.Concat(Enumerable.Repeat("*.", RabbitMQDelayLevels.Count - 1)) + "0.#", RabbitMQDelayLevels.LevelBindingKey(0, set: false));
        Assert.Equal(String.Concat(Enumerable.Repeat("*.", RabbitMQDelayLevels.Count)) + "foundatio.queue.orders", RabbitMQDelayLevels.DeliveryBindingKey("foundatio.queue.orders"));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(1001, 2)]
    [InlineData(2000, 2)]
    public void DelayLevels_ToSeconds_RoundsUpSoNothingIsDeliveredEarly(int milliseconds, long seconds)
    {
        Assert.Equal(seconds, RabbitMQDelayLevels.ToSeconds(TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public void DelayLevels_ToSeconds_CapsAtTheLongestDelay()
    {
        Assert.Equal((long)RabbitMQDelayLevels.MaxDelay.TotalSeconds, RabbitMQDelayLevels.ToSeconds(TimeSpan.FromDays(365 * 20)));
        Assert.StartsWith(String.Concat(Enumerable.Repeat("1.", RabbitMQDelayLevels.Count)), RabbitMQDelayLevels.RoutingKey((long)RabbitMQDelayLevels.MaxDelay.TotalSeconds, "q"), StringComparison.Ordinal);
    }

    [Fact]
    public void Names_OfSubscriptionsAndLongNames_AreDistinctAndFitARoutingKey()
    {
        // Arrange
        var names = new RabbitMQMessageTransportNames("foundatio");
        string tooLong = new('x', 300);

        // Act
        string[] all =
        [
            names.Subscription("a.b", "c"), names.Subscription("a", "b.c"), names.Queue(tooLong + "1"), names.Queue(tooLong + "2"),
            names.Queue("a"), names.Topic("a"), names.DeadLetter(MessageSource.Queue("a")), names.DeadLetter(MessageSource.Subscription("a", "b"))
        ];

        // Assert
        Assert.Equal(all.Length, all.Distinct(StringComparer.Ordinal).Count());
        Assert.All(all, name => Assert.True(RabbitMQDelayLevels.RoutingKey(1, name).Length <= 255, name));
        Assert.Equal("foundatio.subscription.a.b:c", all[0]);
        Assert.Equal(names.Queue(tooLong + "1"), new RabbitMQMessageTransportNames("foundatio").Queue(tooLong + "1"));
    }

    [Fact]
    public void HeaderTableLimit_LeavesRoomInTheFrame()
    {
        Assert.Equal(RabbitMQMessageTransportCodec.MaxHeaderTableBytes, RabbitMQMessageTransportCodec.GetHeaderTableLimit(0));
        Assert.Equal(RabbitMQMessageTransportCodec.MaxHeaderTableBytes, RabbitMQMessageTransportCodec.GetHeaderTableLimit(131_072));
        Assert.Equal(0, RabbitMQMessageTransportCodec.GetHeaderTableLimit(4096));
    }
}

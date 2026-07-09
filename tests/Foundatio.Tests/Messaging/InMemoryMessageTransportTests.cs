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
    public void DestinationAddress_KeyEncodesTopicAndSubscription()
    {
        var destination = DestinationAddress.ForSubscription("orders", "sub-a");
        Assert.Equal("orders/sub-a", destination.Key);
        Assert.Equal("orders", destination.Topic);
        Assert.Equal("sub-a", destination.Name);
        Assert.Equal(DestinationRole.Subscription, destination.Role);

        // A bare (non-subscription) destination has no topic and a bare key.
        var bare = DestinationAddress.ForQueue("orders");
        Assert.Null(bare.Topic);
        Assert.Equal("orders", bare.Key);
        Assert.NotEqual(destination, bare);
    }

    [Fact]
    public void MessageHeaders_SerializeToJson_RoundTripsCaseInsensitively()
    {
        var headers = MessageHeaders.Create([
            new KeyValuePair<string, string>("Message.Type", "order.created"),
            new KeyValuePair<string, string>("tenant", "acme")
        ]);

        // The shared codec both transports use preserves the case-insensitive contract across the wire.
        var roundTripped = MessageHeaders.DeserializeFromJson(MessageHeaders.SerializeToJson(headers));
        Assert.Equal("order.created", roundTripped["MESSAGE.TYPE"]);
        Assert.Equal("acme", roundTripped["tenant"]);

        Assert.Empty(MessageHeaders.DeserializeFromJson(null));
        Assert.Empty(MessageHeaders.DeserializeFromJson(""));
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

}

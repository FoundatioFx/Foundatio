using System;
using Foundatio.Messaging.Transport;
using Foundatio.Xunit;
using Xunit;

namespace Foundatio.RabbitMQ.Tests.Messaging;

public class RabbitMQMessageTransportOptionsTests(ITestOutputHelper output) : TestWithLoggingBase(output)
{
    private const string ConnectionString = "amqp://guest:guest@localhost:5672";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://localhost:5672")]
    [InlineData("not a uri")]
    public void Constructor_WithoutAnAmqpConnectionString_Throws(string? connectionString)
    {
        Assert.Throws<ArgumentException>(() => new RabbitMQMessageTransport(new RabbitMQMessageTransportOptions { ConnectionString = connectionString }));
    }

    [Theory]
    [InlineData("amq")]
    [InlineData("amq.foundatio")]
    [InlineData("AMQ.x")]
    [InlineData("has space")]
    [InlineData("colon:prefix")]
    [InlineData("")]
    public void Constructor_WithAnInvalidPrefix_Throws(string prefix)
    {
        Assert.Throws<ArgumentException>(() => new RabbitMQMessageTransport(new RabbitMQMessageTransportOptions { ConnectionString = ConnectionString, Prefix = prefix }));
    }

    [Fact]
    public void Constructor_WithAMaxMessageBytesBelowTheEncodingReserve_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RabbitMQMessageTransport(new RabbitMQMessageTransportOptions { ConnectionString = ConnectionString, MaxMessageBytes = 1024 }));
    }

    [Fact]
    public void Capabilities_WithDefaultOptions_DeclareDelaysAndNoLeaseExpiration()
    {
        // Act
        var capabilities = new RabbitMQMessageTransport(new RabbitMQMessageTransportOptions { ConnectionString = ConnectionString }).Capabilities;

        // Assert
        Assert.Equal(TimeSpan.FromSeconds((1L << 28) - 1), capabilities.MaxDelay);
        Assert.Equal(capabilities.MaxDelay, capabilities.MaxPublishDelay);
        Assert.Equal(capabilities.MaxDelay, capabilities.MaxRetryDelay);
        Assert.Equal(TimeSpan.FromSeconds(1), capabilities.MinRetryDelay);
        Assert.Equal(16 * 1024 * 1024 - 16 * 1024, capabilities.MaxMessageSize);
        Assert.False(capabilities.LeaseExpiration);
        Assert.Null(capabilities.MaxLeaseDuration);
        Assert.False(capabilities.OrderedGroups);
        Assert.False(capabilities.Deduplication);
    }

    [Fact]
    public void Capabilities_WithoutDelayedDelivery_DeclareNoDelays()
    {
        // Act
        var capabilities = new RabbitMQMessageTransport(new RabbitMQMessageTransportOptions { ConnectionString = ConnectionString, DelayedDelivery = false }).Capabilities;

        // Assert
        Assert.Equal(TimeSpan.Zero, capabilities.MaxDelay);
        Assert.Equal(TimeSpan.Zero, capabilities.MaxPublishDelay);
        Assert.Equal(TimeSpan.Zero, capabilities.MaxRetryDelay);
        Assert.Equal(TimeSpan.Zero, capabilities.MinRetryDelay);
    }
}

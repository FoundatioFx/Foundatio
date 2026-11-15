using Foundatio.Messaging.Transport;
using RabbitMQ.Client;
using Xunit;

namespace Foundatio.RabbitMQ.Tests.Messaging;

public class RabbitMQDelayLevelsTests
{
    [Theory]
    [InlineData(1L, 0)]
    [InlineData(2L, 1)]
    [InlineData(3L, 1)]
    [InlineData(4L, 2)]
    [InlineData(7L, 2)]
    [InlineData(60L, 5)]
    [InlineData((1L << 20) - 1, 19)]
    [InlineData(1L << 20, 20)]
    [InlineData((1L << 27) - 1, 26)]
    [InlineData(1L << 27, 27)]
    [InlineData((1L << RabbitMQDelayLevels.Count) - 1, 27)]
    public void Delayed_ForADelay_EntersAtTheLevelOfItsHighestSetBit(long seconds, int level)
    {
        var names = new RabbitMQMessageTransportNames("app");

        var publish = RabbitMQMessageTransport.Publish.Delayed(names, "app.queue.orders", seconds, new BasicProperties(), new byte[1]);

        Assert.Equal(names.DelayLevel(level), publish.Exchange);
        Assert.Equal(RabbitMQDelayLevels.RoutingKey(seconds, "app.queue.orders"), publish.RoutingKey);
        Assert.True(publish.Mandatory);
    }
}

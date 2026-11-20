using System.Linq;
using Foundatio.Messaging.Transport;
using Xunit;

namespace Foundatio.RabbitMQ.Tests.Messaging;

public class RabbitMQMessageTransportNamesTests
{
    [Fact]
    public void Landing_ForAQueueOrTopic_IsNamedAfterItUnderThePrefix()
    {
        var names = new RabbitMQMessageTransportNames("app");

        string queue = names.Landing(names.Queue("orders"));
        string topic = names.Landing(names.Topic("orders"));

        Assert.Equal("app.landing.queue.orders", queue);
        Assert.Equal("app.landing.topic.orders", topic);
        Assert.Equal(names.Queue("orders"), names.LandingTarget(queue));
        Assert.Equal(names.Topic("orders"), names.LandingTarget(topic));
    }

    [Fact]
    public void Landing_WithLongTargetNames_StaysWithinTheLimitAndIsDistinct()
    {
        var names = new RabbitMQMessageTransportNames(new string('p', 64));
        string[] targets =
        [
            names.Queue(new string('a', 300) + "1"),
            names.Queue(new string('a', 300) + "2"),
            names.Subscription(new string('a', 300), "1"),
            names.Queue(new string('b', RabbitMQMessageTransportNames.MaxNameLength - 64 - ".queue.".Length))
        ];

        string[] landings = targets.Select(names.Landing).ToArray();

        Assert.All(landings, landing => Assert.True(landing.Length <= RabbitMQMessageTransportNames.MaxNameLength, landing));
        Assert.Equal(landings.Length, landings.Distinct().Count());
        Assert.DoesNotContain(landings, targets.Contains);
        Assert.All(landings, landing => Assert.Null(names.LandingTarget(landing)));
    }

    [Theory]
    [InlineData("app.queue.orders")]
    [InlineData("app.landing.")]
    [InlineData("other.landing.queue.orders")]
    public void LandingTarget_OfAnotherName_IsNull(string name)
    {
        var names = new RabbitMQMessageTransportNames("app");

        Assert.Null(names.LandingTarget(name));
    }
}

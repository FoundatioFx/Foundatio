using Xunit;

namespace Foundatio.RabbitMQ.Tests.Messaging;

public class RabbitMqMessageBusClassicDelayedExchangeTests : RabbitMqMessageBusClassicTestBase, IClassFixture<AspireFixture>
{
    public RabbitMqMessageBusClassicDelayedExchangeTests(AspireFixture fixture, ITestOutputHelper output)
        : base(fixture.MessagingDelayedConnectionString ?? fixture.MessagingConnectionString!, output)
    {
        Assert.SkipWhen(string.IsNullOrEmpty(fixture.MessagingDelayedConnectionString),
            "Delayed exchange container not available");
    }
}

using Xunit;

namespace Foundatio.RabbitMQ.Tests.Messaging;

public class RabbitMqMessageBusDelayedExchangeTests : RabbitMqMessageBusTestBase, IClassFixture<AspireFixture>
{
    public RabbitMqMessageBusDelayedExchangeTests(AspireFixture fixture, ITestOutputHelper output)
        : base(fixture.MessagingDelayedConnectionString ?? fixture.MessagingConnectionString!, output)
    {
        Assert.SkipWhen(string.IsNullOrEmpty(fixture.MessagingDelayedConnectionString),
            "Delayed exchange container not available");
    }
}

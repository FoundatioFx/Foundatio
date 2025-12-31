using Xunit;

namespace Foundatio.RabbitMQ.Tests.Messaging;

public class RabbitMqMessageBusClassicDelayedExchangeTests : RabbitMqMessageBusClassicTestBase
{
    public RabbitMqMessageBusClassicDelayedExchangeTests(ITestOutputHelper output) : base("amqp://localhost:5673", output)
    {
    }
}

using Xunit.Abstractions;

namespace Foundatio.RabbitMQ.Tests.Messaging;

public class RabbitMqMessageBusDelayedExchangeTests : RabbitMqMessageBusTestBase
{
    public RabbitMqMessageBusDelayedExchangeTests(ITestOutputHelper output) : base("amqp://localhost:5673", output) { }
}

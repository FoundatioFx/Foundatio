using Xunit.Abstractions;

namespace Foundatio.RabbitMQ.Tests.Messaging;

public class RabbitMqMessageBusClassicTests : RabbitMqMessageBusClassicTestBase
{
    public RabbitMqMessageBusClassicTests(ITestOutputHelper output) : base("amqp://localhost:5672", output)
    {
    }
}

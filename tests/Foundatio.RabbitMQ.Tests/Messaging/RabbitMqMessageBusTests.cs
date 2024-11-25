using Xunit.Abstractions;

namespace Foundatio.RabbitMQ.Tests.Messaging;

public class RabbitMqMessageBusTests : RabbitMqMessageBusTestBase
{
    public RabbitMqMessageBusTests(ITestOutputHelper output) : base("amqp://localhost:5672", output) { }
}

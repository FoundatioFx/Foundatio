using Foundatio.Messaging.Transport;
using Foundatio.Tests.Messaging;
using Xunit;

namespace Foundatio.RabbitMQ.Tests.Messaging;

public class RabbitMQMessageTransportTests(RabbitMQTransportFixture fixture, ITestOutputHelper output) : MessageTransportTestsBase(output)
{
    internal const string Prefix = "foundatio-tests";

    protected RabbitMQTransportFixture Fixture { get; } = fixture;

    protected override IMessageTransport CreateTransport() => new RabbitMQMessageTransport(CreateOptions());

    protected RabbitMQMessageTransportOptions CreateOptions() => new()
    {
        ConnectionString = Fixture.ConnectionString,
        Prefix = Prefix,
        LoggerFactory = Log
    };
}

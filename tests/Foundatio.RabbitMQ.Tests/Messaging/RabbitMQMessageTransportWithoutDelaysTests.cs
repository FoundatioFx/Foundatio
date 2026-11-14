using Xunit;

namespace Foundatio.RabbitMQ.Tests.Messaging;

/// <summary>Runs the conformance suite and the RabbitMQ tests with delayed delivery off, so Foundatio holds retries in-process and rejects delays.</summary>
public class RabbitMQMessageTransportWithoutDelaysTests(RabbitMQTransportFixture fixture, ITestOutputHelper output) : RabbitMQMessageTransportTests(fixture, output)
{
    protected override bool DelayedDelivery => false;
}

using Xunit;

namespace Foundatio.RabbitMQ.Tests.Messaging;

public class RabbitMqMessageBusClassicTests(AspireFixture fixture, ITestOutputHelper output)
    : RabbitMqMessageBusClassicTestBase(fixture.MessagingConnectionString!, output), IClassFixture<AspireFixture>;

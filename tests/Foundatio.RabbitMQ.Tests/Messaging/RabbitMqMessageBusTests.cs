using Xunit;

namespace Foundatio.RabbitMQ.Tests.Messaging;

public class RabbitMqMessageBusTests(AspireFixture fixture, ITestOutputHelper output)
    : RabbitMqMessageBusTestBase(fixture.MessagingConnectionString!, output), IClassFixture<AspireFixture>;

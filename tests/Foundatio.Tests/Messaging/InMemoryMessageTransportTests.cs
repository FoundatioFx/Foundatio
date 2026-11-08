using Foundatio.Messaging.Transport;
using Xunit;

namespace Foundatio.Tests.Messaging;

public class InMemoryMessageTransportTests(ITestOutputHelper output) : MessageTransportTestsBase(output)
{
    protected override IMessageTransport CreateTransport() => new InMemoryMessageTransport();
}

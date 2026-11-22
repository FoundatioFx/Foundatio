using Foundatio.Messaging.Transport;
using Xunit;

namespace Foundatio.Tests.Messaging;

/// <summary>
/// The conformance suite against the in-memory transport handing out one message of a group at a time, as Redis does.
/// </summary>
public class InMemoryMessageTransportWithOneOutstandingPerGroupTests(ITestOutputHelper output) : InMemoryMessageTransportTests(output)
{
    protected override TransportCapabilities EmulatedCapabilities { get; } = new()
    {
        MaxDelay = null,
        MaxSendBatchSize = 1000,
        MaxReceiveBatchSize = 1000,
        LeaseExpiration = true,
        OrderedGroups = true,
        OneOutstandingPerGroup = true,
        Deduplication = true
    };
}

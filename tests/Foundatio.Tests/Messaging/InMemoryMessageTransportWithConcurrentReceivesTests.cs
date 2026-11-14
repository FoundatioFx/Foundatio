using Foundatio.Messaging.Transport;
using Xunit;

namespace Foundatio.Tests.Messaging;

/// <summary>
/// The conformance suite against the in-memory transport allowing concurrent receives on one receiver, with small
/// receive batches so the end-to-end tests run several receives at once.
/// </summary>
public class InMemoryMessageTransportWithConcurrentReceivesTests(ITestOutputHelper output) : InMemoryMessageTransportTests(output)
{
    protected override TransportCapabilities EmulatedCapabilities { get; } = new()
    {
        MaxDelay = null,
        MaxSendBatchSize = 1000,
        MaxReceiveBatchSize = 10,
        MaxConcurrentReceives = 4,
        OrderedGroups = true,
        Deduplication = true
    };
}

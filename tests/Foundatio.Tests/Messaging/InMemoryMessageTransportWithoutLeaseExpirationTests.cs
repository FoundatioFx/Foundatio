using Foundatio.Messaging.Transport;
using Xunit;

namespace Foundatio.Tests.Messaging;

/// <summary>The conformance suite against the in-memory transport emulating an offset-based broker such as Kafka: leases never expire, and there is no deduplication.</summary>
public class InMemoryMessageTransportWithoutLeaseExpirationTests(ITestOutputHelper output) : InMemoryMessageTransportTests(output)
{
    protected override TransportCapabilities EmulatedCapabilities { get; } = new()
    {
        MaxDelay = null,
        MaxSendBatchSize = 1000,
        MaxReceiveBatchSize = 100,
        MaxMessageSize = 1024 * 1024,
        LeaseExpiration = false,
        OrderedGroups = true
    };
}

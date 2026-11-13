using Xunit;

namespace Foundatio.AzureServiceBus.Tests.Messaging;

/// <summary>Runs the conformance suite and the Service Bus tests against queues and topics with duplicate detection, plus what only it does.</summary>
public class AzureServiceBusDuplicateDetectionMessageTransportTests(ITestOutputHelper output) : AzureServiceBusMessageTransportTests(output)
{
    protected override bool RequiresDuplicateDetection => true;
}

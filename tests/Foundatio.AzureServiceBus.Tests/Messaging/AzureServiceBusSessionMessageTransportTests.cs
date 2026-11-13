using Xunit;

namespace Foundatio.AzureServiceBus.Tests.Messaging;

/// <summary>Runs the conformance suite and the Service Bus tests against session-enabled queues and subscriptions, plus what only sessions do.</summary>
public class AzureServiceBusSessionMessageTransportTests(ITestOutputHelper output) : AzureServiceBusMessageTransportTests(output)
{
    protected override bool RequiresSession => true;
}

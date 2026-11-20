using Azure.Messaging.ServiceBus;

namespace Foundatio.Messaging.Transport;

/// <summary>A delivery from Service Bus, with the received message whose lock token settles it.</summary>
internal sealed class AzureServiceBusIncomingMessage : IncomingMessage
{
    /// <summary>The receiver that handed it out; only it can settle it.</summary>
    public required object Owner { get; init; }

    public required ServiceBusReceivedMessage Received { get; init; }

    /// <summary>The session it came from, on a session entity.</summary>
    public AzureServiceBusSession? Session { get; init; }
}

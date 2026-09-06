using System;

namespace Foundatio.Messaging;

/// <summary>A destination disappeared. Ensure-mode listeners can recreate it under supervision.</summary>
public sealed class MessageDestinationNotFoundException(DestinationAddress destination, Exception innerException)
    : MessageBusException($"Message destination {destination} no longer exists.", innerException)
{
    public DestinationAddress Destination { get; } = destination;
}

using System;

namespace Foundatio.Messaging.Transport;

/// <summary>Thrown by a transport when a delivery's lease has expired or moved to another receiver.</summary>
public class MessageLeaseLostException : MessageBusException
{
    public MessageLeaseLostException(string message) : base(message)
    {
    }

    public MessageLeaseLostException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

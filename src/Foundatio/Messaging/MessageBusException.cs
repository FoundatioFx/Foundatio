using System;

namespace Foundatio.Messaging;

/// <summary>
/// Exception thrown when a message bus operation fails.
/// </summary>
public class MessageBusException : Exception
{
    public MessageBusException(string message) : base(message)
    {
    }

    public MessageBusException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

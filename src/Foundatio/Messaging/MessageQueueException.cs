using System;

namespace Foundatio.Messaging;

/// <summary>
/// Exception thrown when a message queue operation fails.
/// </summary>
public class MessageQueueException : MessageBusException
{
    public MessageQueueException(string message) : base(message)
    {
    }

    public MessageQueueException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

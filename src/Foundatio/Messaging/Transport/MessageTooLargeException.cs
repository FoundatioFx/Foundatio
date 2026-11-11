using System;

namespace Foundatio.Messaging.Transport;

/// <summary>Thrown when a message is larger than <see cref="TransportCapabilities.MaxMessageSize"/>.</summary>
public class MessageTooLargeException : MessageBusException
{
    public MessageTooLargeException(string message) : base(message)
    {
    }

    public MessageTooLargeException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>The size of the rejected message in bytes, when known.</summary>
    public long? Size { get; init; }

    /// <summary>The transport's limit in bytes, when known.</summary>
    public long? Limit { get; init; }
}

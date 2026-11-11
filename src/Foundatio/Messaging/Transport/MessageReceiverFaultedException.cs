using System;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// Thrown by <see cref="IMessageReceiver.ReceiveAsync"/> when the receiver can no longer deliver, for example after a
/// fatal client error. Foundatio lets the receiver's in-flight messages finish, disposes it and opens a new one.
/// </summary>
public class MessageReceiverFaultedException : MessageBusException
{
    public MessageReceiverFaultedException(string message) : base(message)
    {
    }

    public MessageReceiverFaultedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

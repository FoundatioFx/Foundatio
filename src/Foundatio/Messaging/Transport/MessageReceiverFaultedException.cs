using System;
using System.Diagnostics.CodeAnalysis;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// Thrown by <see cref="IMessageReceiver.ReceiveAsync"/> when the receiver can no longer deliver, for example after a
/// fatal client error. Foundatio lets the receiver's in-flight messages finish, disposes it and opens a new one.
/// </summary>
[Experimental(ProviderApi.DiagnosticId, UrlFormat = ProviderApi.UrlFormat)]
public class MessageReceiverFaultedException : MessageBusException
{
    /// <summary>Creates the exception with an error message.</summary>
    public MessageReceiverFaultedException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with an error message and the exception that caused it.</summary>
    public MessageReceiverFaultedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

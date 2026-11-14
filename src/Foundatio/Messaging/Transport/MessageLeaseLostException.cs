using System;
using System.Diagnostics.CodeAnalysis;

namespace Foundatio.Messaging.Transport;

/// <summary>Thrown by a transport when a delivery's lease has expired or moved to another receiver.</summary>
[Experimental(ProviderApi.DiagnosticId, UrlFormat = ProviderApi.UrlFormat)]
public class MessageLeaseLostException : MessageBusException
{
    /// <summary>Creates the exception with an error message.</summary>
    public MessageLeaseLostException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with an error message and the exception that caused it.</summary>
    public MessageLeaseLostException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

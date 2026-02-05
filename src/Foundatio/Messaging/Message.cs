using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Foundatio.Messaging;

/// <summary>
/// Represents a message received from the message bus with metadata and raw payload.
/// Subscribe to <see cref="IMessage"/> to receive all message types.
/// </summary>
public interface IMessage
{
    /// <summary>
    /// Gets the unique identifier for this message instance.
    /// </summary>
    string UniqueId { get; }

    /// <summary>
    /// Gets the correlation identifier for distributed tracing.
    /// </summary>
    string CorrelationId { get; }

    /// <summary>
    /// Gets the message type name used for routing.
    /// </summary>
    string Type { get; }

    /// <summary>
    /// Gets the CLR type of the message payload, or null if the type cannot be resolved.
    /// </summary>
    Type ClrType { get; }

    /// <summary>
    /// Gets the raw serialized message payload.
    /// </summary>
    byte[] Data { get; }

    /// <summary>
    /// Deserializes and returns the message payload.
    /// </summary>
    object GetBody();

    /// <summary>
    /// Gets custom properties attached to this message.
    /// </summary>
    IDictionary<string, string> Properties { get; }
}

/// <summary>
/// A typed message providing strongly-typed access to the message payload.
/// </summary>
/// <typeparam name="T">The type of message payload.</typeparam>
public interface IMessage<T> : IMessage where T : class
{
    /// <summary>
    /// Gets the deserialized message payload.
    /// </summary>
    T Body { get; }
}

[DebuggerDisplay("Type: {Type}")]
public class Message : IMessage
{
    private readonly Func<IMessage, object> _getBody;

    public Message(byte[] data, Func<IMessage, object> getBody)
    {
        Data = data;
        _getBody = getBody;
    }

    public string UniqueId { get; set; }
    public string CorrelationId { get; set; }
    public string Type { get; set; }
    public Type ClrType { get; set; }
    public IDictionary<string, string> Properties { get; set; } = new Dictionary<string, string>();
    public byte[] Data { get; set; }
    public object GetBody() => _getBody(this);
}

public class Message<T> : IMessage<T> where T : class
{
    private readonly IMessage _message;

    public Message(IMessage message)
    {
        _message = message;
    }

    public byte[] Data => _message.Data;

    public T Body => (T)GetBody();

    public string UniqueId => _message.UniqueId;

    public string CorrelationId => _message.CorrelationId;

    public string Type => _message.Type;

    public Type ClrType => _message.ClrType;

    public IDictionary<string, string> Properties => _message.Properties;

    public object GetBody() => _message.GetBody();
}

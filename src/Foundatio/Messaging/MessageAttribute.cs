using System;

namespace Foundatio.Messaging;

/// <summary>Gives a message type a stable name and, optionally, the queue or topic it is routed to.</summary>
/// <remarks>
/// The name identifies the type on the wire, so renaming or moving the class does not strand messages in flight.
/// Without this attribute the name is the type's full name. Names may contain letters, digits, '.', '_' and '-'.
/// </remarks>
/// <example><code>[Message("orders.order-placed")] public record OrderPlaced(int OrderId);</code></example>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class MessageAttribute : Attribute
{
    public MessageAttribute(string? name = null)
    {
        Name = name;
    }

    public string? Name { get; }

    /// <summary>The queue <see cref="IMessageClient.SendAsync{T}"/> delivers to. Defaults to the message name.</summary>
    public string? Queue { get; set; }

    /// <summary>The topic <see cref="IMessageClient.PublishAsync{T}"/> delivers to. Defaults to the message name.</summary>
    public string? Topic { get; set; }
}

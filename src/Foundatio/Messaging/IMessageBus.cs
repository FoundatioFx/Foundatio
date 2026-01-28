using System;
using System.Collections.Generic;

namespace Foundatio.Messaging;

/// <summary>
/// Represents a message bus that supports both publishing and subscribing to messages.
/// </summary>
public interface IMessageBus : IMessagePublisher, IMessageSubscriber, IDisposable
{
}

/// <summary>
/// Options for configuring message publishing behavior.
/// </summary>
public record MessageOptions
{
    /// <summary>
    /// Gets or sets a unique identifier for the message.
    /// Can be used for message deduplication and idempotency checks.
    /// </summary>
    public string UniqueId { get; set; }

    /// <summary>
    /// Gets or sets the correlation identifier for distributed tracing.
    /// If not set, this is automatically populated from <see cref="System.Diagnostics.Activity.Current"/>
    /// when the message is published, enabling end-to-end request tracing across services.
    /// </summary>
    public string CorrelationId { get; set; }

    /// <summary>
    /// Gets or sets the delay before the message is delivered to subscribers.
    /// <para>
    /// Support for delayed delivery varies by provider. Some providers support native delayed delivery
    /// where messages are persisted and survive application restarts. Other providers use an in-memory
    /// fallback where messages are held in memory until the delay expires.
    /// </para>
    /// <para>
    /// <strong>Warning:</strong> For providers using the in-memory fallback, delayed messages are not persisted
    /// and will be lost if the application restarts before the delay expires. Check your provider's
    /// documentation for specific behavior.
    /// </para>
    /// </summary>
    public TimeSpan? DeliveryDelay { get; set; }

    /// <summary>
    /// Gets or sets custom properties to include with the message.
    /// These properties are propagated through the message bus and available to subscribers.
    /// The mechanism for propagation varies by provider implementation.
    /// </summary>
    public IDictionary<string, string> Properties { get; set; } = new Dictionary<string, string>();
}

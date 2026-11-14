using System;
using Foundatio.Resilience;
using Foundatio.Serializer;

namespace Foundatio.Messaging;

/// <summary>Settings shared by all handlers.</summary>
public sealed class MessagingOptions
{
    /// <summary>
    /// Names this service's durable subscriptions, which the broker keeps under that name: services that share it split
    /// each other's events, and renaming it starts new subscriptions. Required when a service-scoped subscriber has no
    /// <see cref="SubscriptionOptions.Name"/>, unless the transport is <see cref="Transport.InMemoryMessageTransport"/>,
    /// which defaults it to the host's application name.
    /// </summary>
    public string? ServiceName { get; set; }

    /// <summary>Used when a handler does not set its own policy.</summary>
    public RetryPolicy DefaultRetryPolicy { get; set; } = RetryPolicy.Default;

    /// <summary>How long shutdown waits for in-flight messages before cancelling them. They are then delivered again.</summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(25);

    /// <summary>Defaults to the registered <see cref="ISerializer"/>, or JSON.</summary>
    public ISerializer? Serializer { get; set; }

    /// <summary>
    /// When true, sending or publishing with a <see cref="DeliveryOptions.GroupKey"/> on a transport without
    /// <see cref="Transport.TransportCapabilities.OrderedGroups"/>, or with a <see cref="DeliveryOptions.DeduplicationId"/>
    /// on one without <see cref="Transport.TransportCapabilities.Deduplication"/>, throws <see cref="MessageBusException"/>
    /// instead of sending. When false (the default), the message is sent and a warning is logged once per message type.
    /// </summary>
    public bool RequireTransportCapabilities { get; set; }

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(DefaultRetryPolicy);
        ArgumentOutOfRangeException.ThrowIfLessThan(ShutdownTimeout, TimeSpan.Zero);
    }
}

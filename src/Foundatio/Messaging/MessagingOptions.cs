using System;
using Foundatio.Resilience;
using Foundatio.Serializer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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

    /// <summary>
    /// How long shutdown waits for in-flight messages before cancelling them; they are then delivered again. Handlers
    /// still running get 5 more seconds after cancellation. The wait starts when the host begins stopping and runs
    /// alongside the job worker's, so keep this plus 5 seconds under the host's <c>HostOptions.ShutdownTimeout</c>
    /// (30 seconds by default) and the orchestrator's grace period. Defaults to 20 seconds.
    /// </summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Defaults to the registered <see cref="ISerializer"/>, or JSON.</summary>
    public ISerializer? Serializer { get; set; }

    /// <summary>
    /// When true, sending or publishing with a <see cref="DeliveryOptions.GroupKey"/> on a transport without
    /// <see cref="Transport.TransportCapabilities.OrderedGroups"/>, or with a <see cref="DeliveryOptions.DeduplicationId"/>
    /// on one without <see cref="Transport.TransportCapabilities.Deduplication"/>, throws <see cref="MessageBusException"/>
    /// instead of sending. When false (the default), the message is sent and a warning is logged once per message type.
    /// </summary>
    public bool RequireTransportCapabilities { get; set; }

    /// <summary>
    /// The clock messaging runs on: delivery times, delays, leases and retry backoff. Defaults to the registered
    /// <see cref="System.TimeProvider"/>, or the system clock. The transport <c>UseInMemory()</c> creates uses it too.
    /// Set it to <see cref="System.TimeProvider.System"/> to keep messaging on real time while tests move the app's clock:
    /// a lease taken on a clock moved ahead, then back, would otherwise hold its message for the time it was moved.
    /// </summary>
    public TimeProvider? TimeProvider { get; set; }

    /// <summary>The clock messaging uses in <paramref name="services"/>: <see cref="TimeProvider"/>, the registered one, or the system clock.</summary>
    internal static TimeProvider ResolveTimeProvider(IServiceProvider services)
    {
        return services.GetService<IOptions<MessagingOptions>>()?.Value.TimeProvider ?? services.GetService<TimeProvider>() ?? System.TimeProvider.System;
    }

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(DefaultRetryPolicy);
        ArgumentOutOfRangeException.ThrowIfLessThan(ShutdownTimeout, TimeSpan.Zero);
    }
}

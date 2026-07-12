using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Messaging.Legacy;

/// <summary>
/// Migration adapter: implements the legacy publish/subscribe interfaces over the redesigned
/// <see cref="Foundatio.Messaging.IMessageBus"/> so existing consuming code keeps compiling while it migrates.
/// Register with <c>AddFoundatio().Messaging.AddLegacyAdapter()</c> and delete the call once call sites are on the
/// new API — there is no legacy bus implementation behind this, only the mapping.
/// </summary>
/// <remarks>
/// Semantics map as follows. Every legacy subscription is per-instance and published-only, matching the old bus's
/// fan-out of every message to every subscriber in every process. <see cref="MessageOptions.DeliveryDelay"/> maps to
/// a delayed publish (durable through the runtime store when one is configured — an upgrade over the old in-memory
/// timer). <see cref="MessageOptions.CorrelationId"/> and <see cref="MessageOptions.Properties"/> map to the
/// correlation id and headers. <see cref="MessageOptions.UniqueId"/> has no equivalent (broker deduplication does not
/// exist in the new contract) and is ignored. Messages route by their runtime type through the new routing
/// conventions, so a subscriber of a base/interface type only sees derived messages when routing maps them to the
/// same topic (<c>MapTopic</c>/<c>UseDefaultTopic</c>) — the old bus was one implicit shared channel; the new bus is
/// destination-scoped. For the same reason the old raw-envelope (<c>IMessage</c>) tap has no adapter path: subscribe
/// to concrete types, or use the new bus's untyped <c>SubscribeAsync</c> on an explicitly routed topic.
/// </remarks>
public sealed class LegacyMessageBusAdapter : IMessageBus
{
    private readonly Foundatio.Messaging.IMessageBus _bus;
    private readonly ConcurrentQueue<IMessageSubscription> _subscriptions = new();
    private int _isDisposed;

    public LegacyMessageBusAdapter(Foundatio.Messaging.IMessageBus bus)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
    }

    public Task PublishAsync(Type messageType, object message, MessageOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentNullException.ThrowIfNull(message);

        var publishOptions = new MessagePublishOptions
        {
            Delay = options?.DeliveryDelay,
            CorrelationId = options?.CorrelationId,
            Headers = options?.Properties is { Count: > 0 } properties ? MessageHeaders.Create(properties) : null
        };

        return _bus.PublishBatchAsync([message], publishOptions, cancellationToken);
    }

    public async Task SubscribeAsync<T>(Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(handler);

        // The old bus delivered every published message to every subscriber in every process: per-instance,
        // events only. Auto-ack on return, retry on throw now come from the core policy instead of being swallowed.
        var options = new MessageSubscriptionOptions { PerInstance = true, Deliveries = MessageDeliveries.Published };

        var subscription = await _bus.SubscribeAsync<T>((context, token) => handler(context.Message, token), options, cancellationToken).AnyContext();

        _subscriptions.Enqueue(subscription);
        if (cancellationToken.CanBeCanceled)
            cancellationToken.Register(() => _ = subscription.DisposeAsync());
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    // Disposes only the subscriptions this adapter created; the underlying bus is owned by whoever registered it.
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
            return;

        while (_subscriptions.TryDequeue(out var subscription))
            await subscription.DisposeAsync().AnyContext();
    }
}

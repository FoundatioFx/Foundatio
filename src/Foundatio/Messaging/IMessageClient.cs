using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Messaging;

/// <summary>Sends commands to queues and publishes events to topics.</summary>
/// <remarks>
/// <see cref="SendAsync{T}"/> delivers a message to exactly one consumer: replicas of the consuming service compete
/// for it. <see cref="PublishAsync{T}"/> delivers a copy to every subscribing service (and every instance, for
/// instance-scoped subscribers). Delivery is at least once, so handlers must tolerate duplicates.
/// </remarks>
public interface IMessageClient
{
    /// <summary>Sends a message to the queue its type is routed to.</summary>
    Task SendAsync<T>(T message, SendOptions? options = null, CancellationToken cancellationToken = default) where T : class;

    /// <summary>Sends several messages efficiently. Each is routed by its own type.</summary>
    Task SendBatchAsync<T>(IEnumerable<T> messages, SendOptions? options = null, CancellationToken cancellationToken = default) where T : class;

    /// <summary>Publishes an event to the topic its type is routed to.</summary>
    Task PublishAsync<T>(T message, PublishOptions? options = null, CancellationToken cancellationToken = default) where T : class;

    /// <summary>Publishes several events efficiently. Each is routed by its own type.</summary>
    Task PublishBatchAsync<T>(IEnumerable<T> messages, PublishOptions? options = null, CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// Subscribes to published events at runtime until the returned subscription is disposed. By default every
    /// instance receives a copy, which suits pushing events to connected clients or invalidating local state.
    /// </summary>
    Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, IMessageContext, CancellationToken, Task> handler, SubscriptionOptions? options = null, CancellationToken cancellationToken = default) where T : class;
}

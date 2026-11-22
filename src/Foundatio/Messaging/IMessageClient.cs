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
/// <para>
/// A batch that throws may have been partly sent. Sending it again is only safe from duplicates when each message
/// keeps its id: give the batch a <see cref="MessageKeys"/> selector, and on a transport that deduplicates, a
/// <see cref="MessageKeys.DeduplicationId"/>.
/// </para>
/// </remarks>
public interface IMessageClient
{
    /// <summary>Sends a message to the queue its type is routed to.</summary>
    /// <returns>The message id: <see cref="DeliveryOptions.MessageId"/>, or the one generated for it.</returns>
    Task<string> SendAsync<T>(T message, SendOptions? options = null, CancellationToken cancellationToken = default) where T : class;

    /// <summary>Sends several messages efficiently. Each is routed by its own type.</summary>
    /// <returns>The generated message ids, in the order of <paramref name="messages"/>.</returns>
    Task<IReadOnlyList<string>> SendBatchAsync<T>(IEnumerable<T> messages, SendOptions? options = null, CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// Sends several messages efficiently, each with the ids and group key <paramref name="keys"/> returns for it. Each
    /// is routed by its own type. A null <see cref="MessageKeys.GroupKey"/> falls back to <paramref name="options"/>'s.
    /// </summary>
    /// <returns>The message ids, in the order of <paramref name="messages"/>.</returns>
    Task<IReadOnlyList<string>> SendBatchAsync<T>(IEnumerable<T> messages, Func<T, MessageKeys> keys, SendOptions? options = null, CancellationToken cancellationToken = default) where T : class;

    /// <summary>Publishes an event to the topic its type is routed to.</summary>
    /// <returns>The message id: <see cref="DeliveryOptions.MessageId"/>, or the one generated for it.</returns>
    Task<string> PublishAsync<T>(T message, PublishOptions? options = null, CancellationToken cancellationToken = default) where T : class;

    /// <summary>Publishes several events efficiently. Each is routed by its own type.</summary>
    /// <returns>The generated message ids, in the order of <paramref name="messages"/>.</returns>
    Task<IReadOnlyList<string>> PublishBatchAsync<T>(IEnumerable<T> messages, PublishOptions? options = null, CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// Publishes several events efficiently, each with the ids and group key <paramref name="keys"/> returns for it.
    /// Each is routed by its own type. A null <see cref="MessageKeys.GroupKey"/> falls back to <paramref name="options"/>'s.
    /// </summary>
    /// <returns>The message ids, in the order of <paramref name="messages"/>.</returns>
    Task<IReadOnlyList<string>> PublishBatchAsync<T>(IEnumerable<T> messages, Func<T, MessageKeys> keys, PublishOptions? options = null, CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// Subscribes to published events at runtime until the returned subscription is disposed. By default every
    /// instance receives a copy, which suits pushing events to connected clients or invalidating local state.
    /// </summary>
    Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, IMessageContext, CancellationToken, Task> handler, SubscriptionOptions? options = null, CancellationToken cancellationToken = default) where T : class;
}

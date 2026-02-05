using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Messaging;

/// <summary>
/// Subscribes to messages published on the message bus.
/// Handlers receive messages of the subscribed type and any derived types.
/// </summary>
public interface IMessageSubscriber
{
    /// <summary>
    /// Registers a handler to receive messages of the specified type.
    /// The subscription remains active until the cancellation token is triggered.
    /// </summary>
    /// <typeparam name="T">The message type to subscribe to. Also receives messages of derived types.</typeparam>
    /// <param name="handler">
    /// The async function invoked for each received message.
    /// Exceptions thrown by the handler are logged but do not affect other subscribers.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the subscription.</param>
    Task SubscribeAsync<T>(Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken = default) where T : class;
}

public static class MessageBusExtensions
{
    public static Task SubscribeAsync<T>(this IMessageSubscriber subscriber, Func<T, Task> handler, CancellationToken cancellationToken = default) where T : class
    {
        return subscriber.SubscribeAsync<T>((msg, token) => handler(msg), cancellationToken);
    }

    public static Task SubscribeAsync<T>(this IMessageSubscriber subscriber, Action<T> handler, CancellationToken cancellationToken = default) where T : class
    {
        return subscriber.SubscribeAsync<T>((msg, token) =>
        {
            handler(msg);
            return Task.CompletedTask;
        }, cancellationToken);
    }

    public static Task SubscribeAsync(this IMessageSubscriber subscriber, Func<IMessage, CancellationToken, Task> handler, CancellationToken cancellationToken = default)
    {
        return subscriber.SubscribeAsync<IMessage>((msg, token) => handler(msg, token), cancellationToken);
    }

    public static Task SubscribeAsync(this IMessageSubscriber subscriber, Func<IMessage, Task> handler, CancellationToken cancellationToken = default)
    {
        return subscriber.SubscribeAsync((msg, token) => handler(msg), cancellationToken);
    }

    public static Task SubscribeAsync(this IMessageSubscriber subscriber, Action<IMessage> handler, CancellationToken cancellationToken = default)
    {
        return subscriber.SubscribeAsync((msg, token) =>
        {
            handler(msg);
            return Task.CompletedTask;
        }, cancellationToken);
    }
}

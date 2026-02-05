using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Messaging;

/// <summary>
/// Publishes messages to all subscribers listening for the message type.
/// </summary>
public interface IMessagePublisher
{
    /// <summary>
    /// Publishes a message to all subscribers of the specified type.
    /// </summary>
    /// <param name="messageType">The type used to route the message to subscribers.</param>
    /// <param name="message">The message payload to publish.</param>
    /// <param name="options">Optional settings for delivery delay, correlation ID, and custom properties.</param>
    /// <param name="cancellationToken">Token to cancel the publish operation.</param>
    Task PublishAsync(Type messageType, object message, MessageOptions options = null, CancellationToken cancellationToken = default);
}

public static class MessagePublisherExtensions
{
    public static Task PublishAsync<T>(this IMessagePublisher publisher, T message, MessageOptions options = null, CancellationToken cancellationToken = default) where T : class
    {
        return publisher.PublishAsync(typeof(T), message, options, cancellationToken);
    }

    public static Task PublishAsync<T>(this IMessagePublisher publisher, T message, TimeSpan delay, CancellationToken cancellationToken = default) where T : class
    {
        return publisher.PublishAsync(typeof(T), message, new MessageOptions { DeliveryDelay = delay }, cancellationToken);
    }
}

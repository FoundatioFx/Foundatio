using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Messaging;

/// <summary>
/// The primary messaging client. The verb carries the delivery semantic, so handlers are registered without any
/// topology decision (<c>AddFoundatio().Messaging.AddHandler&lt;T, THandler&gt;()</c>):
/// <list type="bullet">
/// <item><see cref="SendAsync{T}"/> — a command / unit of work: exactly one handler instance across the fleet processes
/// it (competing consumers on the message type's queue destination).</item>
/// <item><see cref="PublishAsync{T}"/> — an event: every subscribing service receives one copy on its own subscription,
/// and a scaled service's instances compete for that copy (so side effects happen once per service, not once per
/// replica). A handler registered with <c>PerInstance = true</c> instead receives a copy on every instance.</item>
/// </list>
/// Retry and dead-lettering are core-owned and identical for both verbs: a handler that throws triggers redelivery and,
/// once attempts are exhausted, the dead-letter policy.
/// </summary>
public interface IMessageBus : IAsyncDisposable
{
    /// <summary>Sends a command / unit of work; exactly one handler instance across the fleet processes it.</summary>
    Task<string> SendAsync<T>(T message, MessageSendOptions? options = null, CancellationToken cancellationToken = default) where T : class;
    Task SendBatchAsync<T>(IEnumerable<T> messages, MessageSendOptions? options = null, CancellationToken cancellationToken = default) where T : class;
    Task SendBatchAsync(IEnumerable<object> messages, MessageSendOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Publishes an event; each subscribing service receives one copy (its instances compete).</summary>
    Task PublishAsync<T>(T message, MessagePublishOptions? options = null, CancellationToken cancellationToken = default) where T : class;
    Task PublishBatchAsync<T>(IEnumerable<T> messages, MessagePublishOptions? options = null, CancellationToken cancellationToken = default) where T : class;
    Task PublishBatchAsync(IEnumerable<object> messages, MessagePublishOptions? options = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Facade unifying the queue (send) and pub/sub (publish) clients behind the two delivery verbs. The underlying
/// clients remain available for advanced scenarios (pull receive, programmatic consumers/subscriptions).
/// Disposing the bus disposes the underlying clients only when <c>ownsClients</c> is true — default false, since DI
/// singleton clients are disposed exactly once by the container.
/// </summary>
public sealed class MessageBus : IMessageBus
{
    private readonly IQueue _queue;
    private readonly IPubSub _pubSub;
    private readonly bool _ownsClients;

    public MessageBus(IQueue queue, IPubSub pubSub, bool ownsClients = false)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _pubSub = pubSub ?? throw new ArgumentNullException(nameof(pubSub));
        _ownsClients = ownsClients;
    }

    public Task<string> SendAsync<T>(T message, MessageSendOptions? options = null, CancellationToken cancellationToken = default) where T : class
        => _queue.EnqueueAsync(message, options, cancellationToken);

    public Task SendBatchAsync<T>(IEnumerable<T> messages, MessageSendOptions? options = null, CancellationToken cancellationToken = default) where T : class
        => _queue.EnqueueBatchAsync(messages, options, cancellationToken);

    public Task SendBatchAsync(IEnumerable<object> messages, MessageSendOptions? options = null, CancellationToken cancellationToken = default)
        => _queue.EnqueueBatchAsync(messages, options, cancellationToken);

    public Task PublishAsync<T>(T message, MessagePublishOptions? options = null, CancellationToken cancellationToken = default) where T : class
        => _pubSub.PublishAsync(message, options, cancellationToken);

    public Task PublishBatchAsync<T>(IEnumerable<T> messages, MessagePublishOptions? options = null, CancellationToken cancellationToken = default) where T : class
        => _pubSub.PublishBatchAsync(messages, options, cancellationToken);

    public Task PublishBatchAsync(IEnumerable<object> messages, MessagePublishOptions? options = null, CancellationToken cancellationToken = default)
        => _pubSub.PublishBatchAsync(messages, options, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (!_ownsClients)
            return;

        await _queue.DisposeAsync().AnyContext();
        await _pubSub.DisposeAsync().AnyContext();
    }
}

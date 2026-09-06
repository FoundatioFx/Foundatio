using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Messaging;

public sealed record MessageSendOptions
{
    /// <summary>Stable application ID for correlation and consumer deduplication. Null generates an ID. Does not make sends exactly once.</summary>
    public string? MessageId { get; init; }
    public MessagePriority Priority { get; init; } = MessagePriority.Normal;
    public TimeSpan? Delay { get; init; }
    public DateTimeOffset? DeliverAt { get; init; }
    public TimeSpan? TimeToLive { get; init; }
    public string? CorrelationId { get; init; }
    /// <summary>Overrides the routed destination for this send.</summary>
    public string? Destination { get; init; }
    public MessageHeaders? Headers { get; init; }
}

public sealed record MessagePublishOptions
{
    /// <summary>Stable application ID. Null generates an ID. Consumers remain responsible for deduplication.</summary>
    public string? MessageId { get; init; }
    public MessagePriority Priority { get; init; } = MessagePriority.Normal;
    public TimeSpan? Delay { get; init; }
    public DateTimeOffset? DeliverAt { get; init; }
    public TimeSpan? TimeToLive { get; init; }
    public string? CorrelationId { get; init; }
    /// <summary>Overrides the routed topic for this publish.</summary>
    public string? Topic { get; init; }
    public MessageHeaders? Headers { get; init; }
}

/// <summary>Failure handling and concurrency for one receiving endpoint.</summary>
public abstract class MessageHandlerOptions
{
    /// <summary>Optional stable wire name bound with a declarative handler registration.</summary>
    public string? MessageTypeName { get; set; }
    /// <summary>Maximum in-flight messages across this endpoint's handlers on this process. Default 1.</summary>
    public int MaxConcurrency { get; set; } = 1;

    /// <summary>Maximum delivery attempts. Null uses the bus retry policy.</summary>
    public int? MaxAttempts { get; set; }

    /// <summary>Delay after a failed delivery. Null uses the bus retry policy.</summary>
    public Func<int, TimeSpan>? RedeliveryBackoff { get; set; }

    /// <summary>Identifies failures that must be dead-lettered immediately.</summary>
    public Func<Exception, bool>? DeadLetterWhen { get; set; }

    /// <summary>Automatically acknowledge successful handlers, or require explicit settlement.</summary>
    public AckMode AckMode { get; set; } = AckMode.Auto;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConcurrency, 1);
        if (MaxAttempts is { } attempts)
            ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);
        if (!Enum.IsDefined(AckMode))
            throw new ArgumentOutOfRangeException(nameof(AckMode));
        if (this is MessageConsumerOptions { Destination: { } destination })
            ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        if (this is MessageSubscriptionOptions { Topic: { } topic })
            ArgumentException.ThrowIfNullOrWhiteSpace(topic);
    }

    /// <summary>Dead-letters this exception type without retrying. Multiple calls compose.</summary>
    public void DeadLetterOn<TException>() where TException : Exception
    {
        var existing = DeadLetterWhen;
        DeadLetterWhen = existing is null ? static ex => ex is TException : ex => existing(ex) || ex is TException;
    }
}

/// <summary>Options for a competing consumer of queued work.</summary>
public sealed class MessageConsumerOptions : MessageHandlerOptions
{
    /// <summary>Queue name. Null uses the message type's configured route.</summary>
    public string? Destination { get; set; }
}

/// <summary>Options for receiving published events.</summary>
public sealed class MessageSubscriptionOptions : MessageHandlerOptions
{
    /// <summary>Topic name. Null uses the message type's configured route.</summary>
    public string? Topic { get; set; }

    /// <summary>
    /// Stable durable subscription name. Replicas using the same name compete for that subscription's events.
    /// Null creates a temporary subscription with a renewable expiration lease; disposal removes its backlog.
    /// </summary>
    public string? Subscription { get; set; }

    internal MessageSubscriptionOptions Copy() => (MessageSubscriptionOptions)MemberwiseClone();
}

/// <summary>The observable state of a supervised listener.</summary>
public enum MessageSubscriptionStatus { Starting, Healthy, Recovering, Stopped }

/// <summary>A running consumer. Disposal stops receiving and releases the listener's resources.</summary>
public interface IMessageSubscription : IAsyncDisposable
{
    /// <summary>The queue or topic subscription this consumer receives from.</summary>
    DestinationAddress Source { get; }
    /// <summary>Recovering listeners must not be reported as healthy.</summary>
    MessageSubscriptionStatus Status { get; }
    /// <summary>Increases after a possible delivery gap. Derived local state must be resynchronized.</summary>
    long RecoveryVersion { get; }
    /// <summary>Waits until receiving resumes; cancellation stops only the wait.</summary>
    Task WaitUntilReadyAsync(CancellationToken cancellationToken = default);
}

/// <summary>A batch payload with a stable application ID for selective retry.</summary>
public sealed record MessageBatchItem<T>(T Message, string? MessageId = null, MessageHeaders? Headers = null) : IMessageBatchItem where T : class
{
    object IMessageBatchItem.Value => Message;
}

internal interface IMessageBatchItem
{
    object Value { get; }
    string? MessageId { get; }
    MessageHeaders? Headers { get; }
}

public interface IMessageBus : IAsyncDisposable
{
    /// <summary>Whether per-instance, automatically expiring event subscriptions are available.</summary>
    bool SupportsTemporarySubscriptions => false;
    /// <summary>Receives queued work directly. Dispose an unsettled delivery to return it for redelivery.</summary>
    Task<IReceivedMessage<T>?> ReceiveAsync<T>(MessageReceiveOptions? options = null, CancellationToken cancellationToken = default) where T : class;

    /// <summary>Receives a raw message from an explicit queue without deserializing its body.</summary>
    Task<IReceivedMessage?> ReceiveAsync(MessageReceiveOptions options, CancellationToken cancellationToken = default);

    /// <summary>Enqueues work for a competing consumer. Returns its application message ID.</summary>
    Task<string> SendAsync<T>(T message, MessageSendOptions? options = null, CancellationToken cancellationToken = default) where T : class;

    /// <summary>Enqueues work in input order. Batches are not atomic.</summary>
    Task<IReadOnlyList<string>> SendBatchAsync<T>(IEnumerable<T> messages, MessageSendOptions? options = null, CancellationToken cancellationToken = default) where T : class;
    /// <summary>Sends per-input application IDs and headers, preserving outcome order.</summary>
    Task<IReadOnlyList<string>> SendBatchAsync<T>(IEnumerable<MessageBatchItem<T>> messages, MessageSendOptions? options = null, CancellationToken cancellationToken = default) where T : class;
    Task<IReadOnlyList<string>> SendBatchAsync(IEnumerable<object> messages, MessageSendOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Publishes to existing subscriptions. Events without subscriptions are dropped.</summary>
    Task<string> PublishAsync<T>(T message, MessagePublishOptions? options = null, CancellationToken cancellationToken = default) where T : class;

    /// <summary>Publishes events and returns their IDs in input order. Batches are not atomic.</summary>
    Task<IReadOnlyList<string>> PublishBatchAsync<T>(IEnumerable<T> messages, MessagePublishOptions? options = null, CancellationToken cancellationToken = default) where T : class;
    /// <summary>Publishs per-input application IDs and headers, preserving outcome order.</summary>
    Task<IReadOnlyList<string>> PublishBatchAsync<T>(IEnumerable<MessageBatchItem<T>> messages, MessagePublishOptions? options = null, CancellationToken cancellationToken = default) where T : class;
    Task<IReadOnlyList<string>> PublishBatchAsync(IEnumerable<object> messages, MessagePublishOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Consumes queued work. Only one handler per message type may be registered on an endpoint in this bus.</summary>
    Task<IMessageSubscription> ConsumeAsync<T>(Func<IMessageContext<T>, CancellationToken, Task> handler, MessageConsumerOptions? options = null, CancellationToken cancellationToken = default) where T : class;
    Task<IMessageSubscription> ConsumeAsync(Func<IMessageContext, CancellationToken, Task> handler, MessageConsumerOptions options, CancellationToken cancellationToken = default);

    /// <summary>Receives published events. An unnamed subscription is temporary; a named subscription is durable.</summary>
    Task<IMessageSubscription> SubscribeAsync<T>(Func<IMessageContext<T>, CancellationToken, Task> handler, MessageSubscriptionOptions? options = null, CancellationToken cancellationToken = default) where T : class;
    Task<IMessageSubscription> SubscribeAsync(Func<IMessageContext, CancellationToken, Task> handler, MessageSubscriptionOptions options, CancellationToken cancellationToken = default);
}

/// <summary>
/// Governs the messaging client's topology-administration behavior. Publishing and subscribing never implicitly grant
/// themselves more than this mode allows, so an app on a locked-down broker can state "validate only" or "never touch
/// topology" instead of hoping implicit creation fails gracefully.
/// </summary>
public enum TopologyMode
{
    /// <summary>Create missing destinations on first use and at handler-host startup (default).</summary>
    Ensure,

    /// <summary>Never create. Verify each destination exists on first use (cached) and throw when missing.</summary>
    Validate,

    /// <summary>No topology calls at all; destinations are assumed pre-provisioned out of band.</summary>
    None
}

public sealed record MessageBusOptions
{
    /// <summary>How the client administers topology (create on use, validate-only, or never touch). Default <see cref="TopologyMode.Ensure"/>.</summary>
    public TopologyMode Topology { get; init; } = TopologyMode.Ensure;

    public ISerializer Serializer { get; init; } = DefaultSerializer.Instance;
    /// <summary>Media type produced by the serializer. Defaults to JSON for SystemTextJsonSerializer, otherwise byte-safe application/octet-stream.</summary>
    public string? ContentType { get; init; }
    public IMessageRouter Router { get; init; } = DefaultMessageRouter.Instance;
    public IMessageTypeRegistry MessageTypes { get; init; } = new MessageTypeRegistry();
    /// <summary>
    /// Stores delayed sends and retries beyond native transport limits. Start a ScheduledMessageDispatcher
    /// explicitly (AddScheduledMessageDispatcher in hosted apps). Messaging needs only IScheduledDispatchStore;
    /// an IJobRuntimeStore can also supply this contract. Registering storage starts no background services.
    /// </summary>
    public IScheduledDispatchStore? RuntimeStore { get; init; }
    public RetryPolicy RetryPolicy { get; init; } = new();

    /// <summary>
    /// Whether disposing this bus also disposes the transport. True (default) for a transport the bus solely uses; set
    /// false when the transport is a shared/externally-owned instance (e.g. a DI singleton).
    /// </summary>
    public bool OwnsTransport { get; init; } = true;
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public ILoggerFactory? LoggerFactory { get; init; }
}

/// <summary>
/// The one messaging client over the transport. Routing, serialization, settlement, scheduling, and the consumer loop
/// live in <see cref="MessageClientCore"/>; this type maps the two delivery verbs and subscriptions onto that core.
/// </summary>
public sealed class MessageBus : IMessageBus
{
    private readonly MessageClientCore _core;
    private readonly ILogger _logger;

    public MessageBus(IMessageTransport transport, MessageBusOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        options ??= new MessageBusOptions();
        _logger = (options.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger<MessageBus>();
        _core = new MessageClientCore(transport, options.Serializer, options.Router, options.RuntimeStore, options.TimeProvider, _logger,
            static (message, inner) => inner is null ? new MessageBusException(message) : new MessageBusException(message, inner), options.RetryPolicy, options.OwnsTransport, options.MessageTypes, options.ContentType, options.Topology);
    }

    public Task<string> SendAsync<T>(T message, MessageSendOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(message);
        options ??= new MessageSendOptions();
        return _core.SendAsync(ScheduledDispatchKind.QueueMessage, typeof(T), message, ToEnvelope(options), GetDestination(typeof(T), options.Destination), EnsureDestinationAsync, cancellationToken);
    }

    public Task<IReceivedMessage<T>?> ReceiveAsync<T>(MessageReceiveOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        options ??= new MessageReceiveOptions();
        return _core.ReceiveAsync<T>(GetDestination(typeof(T), options.Destination), options.WaitTime, cancellationToken);
    }

    public Task<IReceivedMessage?> ReceiveAsync(MessageReceiveOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Destination);
        return _core.ReceiveAsync(DestinationAddress.ForQueue(options.Destination), options.WaitTime, cancellationToken);
    }

    public Task<IReadOnlyList<string>> SendBatchAsync<T>(IEnumerable<T> messages, MessageSendOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(messages);
        options ??= new MessageSendOptions();
        return _core.SendBatchAsync(ScheduledDispatchKind.QueueMessage, messages.Cast<object>(), typeof(T), ToEnvelope(options), type => GetDestination(type, options.Destination), EnsureDestinationAsync, cancellationToken);
    }

    public Task<IReadOnlyList<string>> SendBatchAsync<T>(IEnumerable<MessageBatchItem<T>> messages, MessageSendOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(messages);
        options ??= new MessageSendOptions();
        return _core.SendBatchAsync(ScheduledDispatchKind.QueueMessage, messages.Cast<object>(), typeof(T), ToEnvelope(options), type => GetDestination(type, options.Destination), EnsureDestinationAsync, cancellationToken);
    }

    public Task<IReadOnlyList<string>> SendBatchAsync(IEnumerable<object> messages, MessageSendOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        options ??= new MessageSendOptions();
        return _core.SendBatchAsync(ScheduledDispatchKind.QueueMessage, messages, null, ToEnvelope(options), type => GetDestination(type, options.Destination), EnsureDestinationAsync, cancellationToken);
    }

    public Task<string> PublishAsync<T>(T message, MessagePublishOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(message);
        options ??= new MessagePublishOptions();
        return _core.SendAsync(ScheduledDispatchKind.PubSubMessage, typeof(T), message, ToEnvelope(options), GetTopic(typeof(T), options.Topic), EnsureDestinationAsync, cancellationToken);
    }

    public Task<IReadOnlyList<string>> PublishBatchAsync<T>(IEnumerable<T> messages, MessagePublishOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(messages);
        options ??= new MessagePublishOptions();
        return _core.SendBatchAsync(ScheduledDispatchKind.PubSubMessage, messages.Cast<object>(), typeof(T), ToEnvelope(options), type => GetTopic(type, options.Topic), EnsureDestinationAsync, cancellationToken);
    }

    public Task<IReadOnlyList<string>> PublishBatchAsync<T>(IEnumerable<MessageBatchItem<T>> messages, MessagePublishOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(messages);
        options ??= new MessagePublishOptions();
        return _core.SendBatchAsync(ScheduledDispatchKind.PubSubMessage, messages.Cast<object>(), typeof(T), ToEnvelope(options), type => GetTopic(type, options.Topic), EnsureDestinationAsync, cancellationToken);
    }

    public Task<IReadOnlyList<string>> PublishBatchAsync(IEnumerable<object> messages, MessagePublishOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        options ??= new MessagePublishOptions();
        return _core.SendBatchAsync(ScheduledDispatchKind.PubSubMessage, messages, null, ToEnvelope(options), type => GetTopic(type, options.Topic), EnsureDestinationAsync, cancellationToken);
    }

    public bool SupportsTemporarySubscriptions => _core.SupportsTemporarySubscriptions;

    public Task<IMessageSubscription> ConsumeAsync<T>(Func<IMessageContext<T>, CancellationToken, Task> handler, MessageConsumerOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        return ConsumeCoreAsync(options ?? new(), typeof(T), (config, token) => _core.StartListenerAsync(config, handler, token), cancellationToken);
    }

    public Task<IMessageSubscription> ConsumeAsync(Func<IMessageContext, CancellationToken, Task> handler, MessageConsumerOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Destination);
        return ConsumeCoreAsync(options, typeof(object), (config, token) => _core.StartListenerAsync(config, handler, token), cancellationToken);
    }

    public Task<IMessageSubscription> SubscribeAsync<T>(Func<IMessageContext<T>, CancellationToken, Task> handler, MessageSubscriptionOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        return SubscribeCoreAsync(options ?? new(), typeof(T), (config, token) => _core.StartListenerAsync(config, handler, token), cancellationToken);
    }

    public Task<IMessageSubscription> SubscribeAsync(Func<IMessageContext, CancellationToken, Task> handler, MessageSubscriptionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Topic);
        return SubscribeCoreAsync(options, typeof(object), (config, token) => _core.StartListenerAsync(config, handler, token), cancellationToken);
    }

    private async Task<IMessageSubscription> ConsumeCoreAsync(MessageConsumerOptions options, Type messageType, Func<ListenerConfig, CancellationToken, Task<MessageListenerHandle>> start, CancellationToken cancellationToken)
    {
        RequireRole(DestinationRole.Queue);
        var source = GetDestination(messageType, options.Destination);
        var config = CreateListener(options, messageType, source);
        await EnsureDestinationAsync(source, cancellationToken).AnyContext();
        return await start(config, cancellationToken).AnyContext();
    }

    private async Task<IMessageSubscription> SubscribeCoreAsync(MessageSubscriptionOptions options, Type messageType, Func<ListenerConfig, CancellationToken, Task<MessageListenerHandle>> start, CancellationToken cancellationToken)
    {
        RequireRole(DestinationRole.Topic);
        RequireRole(DestinationRole.Subscription);
        var topic = GetTopic(messageType, options.Topic);
        bool ephemeral = options.Subscription is null;
        if (!ephemeral)
            ArgumentException.ThrowIfNullOrWhiteSpace(options.Subscription);
        string subscription = options.Subscription ?? $"temporary-{Guid.NewGuid():N}";
        var source = DestinationAddress.ForSubscription(topic.Name, subscription);
        var config = CreateListener(options, messageType, source) with { Ephemeral = ephemeral };
        if (ephemeral)
            _core.RequireEphemeralSubscriptions();
        await _core.EnsureAsync([
            new DestinationDeclaration { Address = topic },
            new DestinationDeclaration { Address = source, AutoDeleteAfter = ephemeral ? TimeSpan.FromMinutes(2) : null }
        ], cancellationToken).AnyContext();
        return await start(config, cancellationToken).AnyContext();
    }

    private static ListenerConfig CreateListener(MessageHandlerOptions options, Type messageType, DestinationAddress source)
    {
        options.Validate();
        return new ListenerConfig
        {
            Source = source,
            Key = messageType.FullName ?? messageType.Name,
            MessageType = messageType,
            AckMode = options.AckMode,
            MaxConcurrency = options.MaxConcurrency,
            MaxAttempts = options.MaxAttempts,
            RedeliveryBackoff = options.RedeliveryBackoff,
            DeadLetterWhen = options.DeadLetterWhen
        };
    }

    private void RequireRole(DestinationRole role)
    {
        if (!_core.SupportsRole(role))
            throw new NotSupportedException($"The transport does not support {role} destinations.");
    }

    public ValueTask DisposeAsync() => _core.DisposeAsync();

    private Task EnsureDestinationAsync(DestinationAddress destination, CancellationToken cancellationToken)
    {
        return _core.EnsureAsync([new DestinationDeclaration { Address = destination }], cancellationToken);
    }

    private DestinationAddress GetDestination(Type messageType, string? destination)
    {
        return DestinationAddress.ForQueue(_core.Router.ResolveRoute(new MessageRouteContext
        {
            MessageType = messageType,
            Role = MessageRouteRole.QueueDestination,
            OperationOverride = destination
        }));
    }

    private DestinationAddress GetTopic(Type messageType, string? topic)
    {
        return DestinationAddress.ForTopic(_core.Router.ResolveRoute(new MessageRouteContext
        {
            MessageType = messageType,
            Role = MessageRouteRole.PubSubTopic,
            OperationOverride = topic
        }));
    }

    private static MessageEnvelopeOptions ToEnvelope(MessageSendOptions options)
    {
        return new MessageEnvelopeOptions
        {
            MessageId = options.MessageId,
            Priority = options.Priority,
            Delay = options.Delay,
            DeliverAt = options.DeliverAt,
            TimeToLive = options.TimeToLive,
            CorrelationId = options.CorrelationId,
            Headers = options.Headers
        };
    }

    private static MessageEnvelopeOptions ToEnvelope(MessagePublishOptions options)
    {
        return new MessageEnvelopeOptions
        {
            MessageId = options.MessageId,
            Priority = options.Priority,
            Delay = options.Delay,
            DeliverAt = options.DeliverAt,
            TimeToLive = options.TimeToLive,
            CorrelationId = options.CorrelationId,
            Headers = options.Headers
        };
    }

}

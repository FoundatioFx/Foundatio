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
    public MessagePriority Priority { get; init; } = MessagePriority.Normal;
    public TimeSpan? Delay { get; init; }
    public DateTimeOffset? DeliverAt { get; init; }
    public TimeSpan? TimeToLive { get; init; }
    public string? CorrelationId { get; init; }
    /// <summary>Overrides the routed topic for this publish.</summary>
    public string? Topic { get; init; }
    public MessageHeaders? Headers { get; init; }
}

/// <summary>
/// The delivery channels a subscription consumes. By default a handler listens on both of its type's channels; a
/// handler that only ever consumes commands (or only events) states that intent so no idle listener is wired — and so
/// a queue-only or topic-only transport can serve it.
/// </summary>
[Flags]
public enum MessageDeliveries
{
    /// <summary>Sent commands from the type's queue-role destination (competing consumers).</summary>
    Sent = 1,

    /// <summary>Published events, delivered via this subscriber's group on the type's topic.</summary>
    Published = 2,

    Both = Sent | Published
}

/// <summary>
/// Options for attaching a handler to a message type — via <c>AddFoundatio().Messaging.AddHandler&lt;T, THandler&gt;(o =&gt; ...)</c>
/// or programmatically via <see cref="IMessageBus.SubscribeAsync{T}"/>. By default a subscription listens on the
/// type's two delivery channels — sent messages (one handler instance across the fleet processes each) and published
/// messages (delivered per the subscription identity below) — narrowed by <see cref="Deliveries"/>.
/// </summary>
public sealed class MessageSubscriptionOptions
{
    /// <summary>
    /// Which delivery channels this subscription consumes. Default <see cref="MessageDeliveries.Both"/>: on a
    /// transport that supports only one channel's roles, the unsupported channel is skipped (logged at debug).
    /// Explicitly requesting a single channel the transport cannot serve throws <see cref="NotSupportedException"/>.
    /// </summary>
    public MessageDeliveries Deliveries { get; set; } = MessageDeliveries.Both;

    /// <summary>
    /// When true, published messages are received by EVERY running instance (each takes a unique subscription),
    /// instead of once per service. For per-instance local state — cache invalidation, config reload. Mutually
    /// exclusive with <see cref="Subscription"/>. Does not affect sent messages, which always go to exactly one instance.
    /// </summary>
    public bool PerInstance { get; set; }

    /// <summary>
    /// The subscriber-group identity for published messages. Defaults to the service identity (plus the
    /// <see cref="SubscriptionQualifier"/> when set), so all instances of a service share one subscription and compete
    /// (each published message is handled once per service). Set an explicit name to form an independent named
    /// subscriber group.
    /// </summary>
    public string? Subscription { get; set; }

    /// <summary>
    /// Distinguishes this subscriber group from others in the same service when no explicit <see cref="Subscription"/>
    /// is set — the default group becomes "{service-identity}.{qualifier}". Set automatically to the handler type name
    /// by <c>AddHandler&lt;T, THandler&gt;</c> so each handler class receives its own copy of published messages.
    /// Ignored when <see cref="Subscription"/> or <see cref="PerInstance"/> is set.
    /// </summary>
    public string? SubscriptionQualifier { get; set; }

    /// <summary>
    /// Maximum messages this subscription processes concurrently per instance. Default 1 — a deliberate divergence
    /// from libraries that default higher: 1 is the only default that preserves per-handler ordering, each handler
    /// already gets its own concurrent stream (10 handlers = 10 parallel consumers), and scaling out replicas scales
    /// throughput without giving up ordering per instance. Raise it for handlers that are I/O-bound and order-agnostic.
    /// </summary>
    public int MaxConcurrency { get; set; } = 1;

    /// <summary>Maximum delivery attempts before dead-lettering. Null uses the default <see cref="RetryPolicy"/>.</summary>
    public int? MaxAttempts { get; set; }

    /// <summary>Delay before each redelivery given the 1-based attempt number. Null uses the default <see cref="RetryPolicy"/>.</summary>
    public Func<int, TimeSpan>? RedeliveryBackoff { get; set; }

    /// <summary>
    /// Marks a handler failure as unrecoverable: when the predicate returns true the message is dead-lettered
    /// immediately instead of retried. Null uses the default <see cref="RetryPolicy"/>. Prefer
    /// <see cref="DeadLetterOn{TException}"/> for the common by-type case.
    /// </summary>
    public Func<Exception, bool>? DeadLetterWhen { get; set; }

    /// <summary>Whether messages auto-complete when the handler returns (default) or are settled manually.</summary>
    public AckMode AckMode { get; set; } = AckMode.Auto;

    /// <summary>Routes by a different type than the handler's type parameter (grouped/interface consumers).</summary>
    public Type? RouteType { get; set; }

    /// <summary>Overrides the routed send destination this subscription listens on.</summary>
    public string? Destination { get; set; }

    /// <summary>Overrides the routed topic this subscription listens on.</summary>
    public string? Topic { get; set; }

    /// <summary>
    /// Consumer identity. Subscriptions sharing a key on the same channel form one consumer group and compete;
    /// defaults to a per-channel key derived from the route. Subscriptions sharing a key must configure identical
    /// failure policies — only the presence of a backoff/DeadLetterWhen is verified, not the delegate itself.
    /// </summary>
    public string? Key { get; set; }

    /// <summary>
    /// Dead-letters failures of type <typeparamref name="TException"/> immediately instead of retrying — for
    /// exceptions a retry can never fix (validation, malformed data). Composes: call once per exception type.
    /// </summary>
    public MessageSubscriptionOptions DeadLetterOn<TException>() where TException : Exception
    {
        var existing = DeadLetterWhen;
        DeadLetterWhen = existing is null
            ? static ex => ex is TException
            : ex => existing(ex) || ex is TException;
        return this;
    }
}

/// <summary>
/// A started subscription; disposing detaches the handler from the message type's delivery channels. Channel-specific
/// properties are empty when that channel was not wired (see <see cref="MessageSubscriptionOptions.Deliveries"/>).
/// </summary>
public interface IMessageSubscription : IAsyncDisposable
{
    /// <summary>Consumer identity; subscriptions sharing a key on a channel form one competing group.</summary>
    string Key { get; }

    /// <summary>The send-channel destination this subscription listens on.</summary>
    string Destination { get; }

    /// <summary>The publish-channel topic this subscription listens on.</summary>
    string Topic { get; }

    /// <summary>The publish-channel subscriber-group identity (service identity unless overridden or per-instance).</summary>
    string Subscription { get; }

    /// <summary>
    /// The publish-channel transport source: the topic-qualified subscription address, so the same subscription
    /// identity on two topics resolves to two distinct sources.
    /// </summary>
    string Source { get; }
}

/// <summary>
/// The messaging client. Handlers are registered without any topology decision and the caller's verb carries the
/// delivery semantic:
/// <list type="bullet">
/// <item><see cref="SendAsync{T}"/> — a command / unit of work: exactly one handler instance across the fleet
/// processes it (competing consumers).</item>
/// <item><see cref="PublishAsync{T}"/> — an event: every subscribing service receives one copy (a scaled service's
/// instances compete for it), or every instance when the subscription opts into
/// <see cref="MessageSubscriptionOptions.PerInstance"/>.</item>
/// </list>
/// Retry and dead-lettering are core-owned and identical for both verbs: a handler that throws triggers redelivery
/// and, once attempts are exhausted, the dead-letter policy.
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

    /// <summary>
    /// Attaches a handler to the message type's delivery channels (sent and published messages). Prefer declarative
    /// registration (<c>AddFoundatio().Messaging.AddHandler&lt;T, THandler&gt;()</c>) for handlers that live for the
    /// app's lifetime; use this for dynamic subscriptions.
    /// </summary>
    Task<IMessageSubscription> SubscribeAsync<T>(Func<IMessageContext<T>, CancellationToken, Task> handler, MessageSubscriptionOptions? options = null, CancellationToken cancellationToken = default) where T : class;
    Task<IMessageSubscription> SubscribeAsync(Func<IMessageContext, CancellationToken, Task> handler, MessageSubscriptionOptions? options = null, CancellationToken cancellationToken = default);
}

public sealed record MessageBusOptions
{
    public ISerializer Serializer { get; init; } = DefaultSerializer.Instance;
    public string ContentType { get; init; } = "application/json";
    public IMessageRouter Router { get; init; } = DefaultMessageRouter.Instance;
    public IMessageTypeRegistry MessageTypes { get; init; } = new MessageTypeRegistry();
    /// <summary>
    /// Enables durable scheduling: delayed sends beyond a transport ceiling and store-parked retry delays are written
    /// here and drained by the job runtime pump. The DI builder registers the pump automatically with the store; when
    /// wiring options by hand, ensure a pump (JobRuntimePumpService / JobScheduleProcessor) is running or parked
    /// messages will never be dispatched.
    /// </summary>
    public IJobRuntimeStore? RuntimeStore { get; init; }
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
            static (message, inner) => inner is null ? new MessageBusException(message) : new MessageBusException(message, inner), options.RetryPolicy, options.OwnsTransport, options.MessageTypes, options.ContentType);
    }

    public Task<string> SendAsync<T>(T message, MessageSendOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(message);
        options ??= new MessageSendOptions();
        return _core.SendAsync(ScheduledDispatchKind.QueueMessage, typeof(T), message, ToEnvelope(options), GetDestination(typeof(T), options.Destination), ensureDestination: null, cancellationToken);
    }

    public Task SendBatchAsync<T>(IEnumerable<T> messages, MessageSendOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(messages);
        options ??= new MessageSendOptions();
        return _core.SendBatchAsync(ScheduledDispatchKind.QueueMessage, messages.Cast<object>(), typeof(T), ToEnvelope(options), type => GetDestination(type, options.Destination), ensureDestination: null, cancellationToken);
    }

    public Task SendBatchAsync(IEnumerable<object> messages, MessageSendOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        options ??= new MessageSendOptions();
        return _core.SendBatchAsync(ScheduledDispatchKind.QueueMessage, messages, null, ToEnvelope(options), type => GetDestination(type, options.Destination), ensureDestination: null, cancellationToken);
    }

    public Task PublishAsync<T>(T message, MessagePublishOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(message);
        options ??= new MessagePublishOptions();
        return _core.SendAsync(ScheduledDispatchKind.PubSubMessage, typeof(T), message, ToEnvelope(options), GetTopic(typeof(T), options.Topic), EnsureTopicAsync, cancellationToken);
    }

    public Task PublishBatchAsync<T>(IEnumerable<T> messages, MessagePublishOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(messages);
        options ??= new MessagePublishOptions();
        return _core.SendBatchAsync(ScheduledDispatchKind.PubSubMessage, messages.Cast<object>(), typeof(T), ToEnvelope(options), type => GetTopic(type, options.Topic), EnsureTopicAsync, cancellationToken);
    }

    public Task PublishBatchAsync(IEnumerable<object> messages, MessagePublishOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        options ??= new MessagePublishOptions();
        return _core.SendBatchAsync(ScheduledDispatchKind.PubSubMessage, messages, null, ToEnvelope(options), type => GetTopic(type, options.Topic), EnsureTopicAsync, cancellationToken);
    }

    public Task<IMessageSubscription> SubscribeAsync<T>(Func<IMessageContext<T>, CancellationToken, Task> handler, MessageSubscriptionOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        return SubscribeCoreAsync(options, typeof(T), (config, token) => _core.StartListenerAsync(config, handler, token), cancellationToken);
    }

    public Task<IMessageSubscription> SubscribeAsync(Func<IMessageContext, CancellationToken, Task> handler, MessageSubscriptionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return SubscribeCoreAsync(options, typeof(object), (config, token) => _core.StartListenerAsync(config, handler, token), cancellationToken);
    }

    private async Task<IMessageSubscription> SubscribeCoreAsync(MessageSubscriptionOptions? options, Type fallbackType, Func<ListenerConfig, CancellationToken, Task<MessageListenerHandle>> start, CancellationToken cancellationToken)
    {
        var deliveries = options?.Deliveries ?? MessageDeliveries.Both;
        if ((deliveries & MessageDeliveries.Both) == 0)
            throw new ArgumentException("Deliveries must include at least one delivery channel.", nameof(options));

        bool wantSent = deliveries.HasFlag(MessageDeliveries.Sent);
        bool wantPublished = deliveries.HasFlag(MessageDeliveries.Published);
        bool canSend = _core.SupportsRole(DestinationRole.Queue);
        bool canPublish = _core.SupportsRole(DestinationRole.Topic) && _core.SupportsRole(DestinationRole.Subscription);

        // An explicit single-channel request the transport cannot serve is a configuration error and must fail loudly;
        // the default Both narrows to whatever the transport supports (a queue-only transport still serves commands)
        // but a transport that can serve neither channel is always an error.
        if (deliveries != MessageDeliveries.Both)
        {
            if (wantSent && !canSend)
                throw new NotSupportedException($"Subscription requests {nameof(MessageDeliveries.Sent)} deliveries, but the transport does not support {DestinationRole.Queue} destinations.");
            if (wantPublished && !canPublish)
                throw new NotSupportedException($"Subscription requests {nameof(MessageDeliveries.Published)} deliveries, but the transport does not support {DestinationRole.Topic} and {DestinationRole.Subscription} destinations.");
        }
        else if (!canSend && !canPublish)
        {
            throw new NotSupportedException("The transport supports neither queue nor topic/subscription destinations; no delivery channel can be wired.");
        }

        bool wireSent = wantSent && canSend;
        bool wirePublished = wantPublished && canPublish;

        if (wantSent && !wireSent)
            _logger.LogDebug("Skipping the sent-message channel for {MessageType}: the transport does not support {Role} destinations", fallbackType.Name, DestinationRole.Queue);
        if (wantPublished && !wirePublished)
            _logger.LogDebug("Skipping the published-message channel for {MessageType}: the transport does not support {TopicRole}/{SubscriptionRole} destinations", fallbackType.Name, DestinationRole.Topic, DestinationRole.Subscription);

        var channels = BuildChannels(options, fallbackType);
        MessageListenerHandle? sent = wireSent ? await start(channels.Send, cancellationToken).AnyContext() : null;
        try
        {
            MessageListenerHandle? published = null;
            if (wirePublished)
            {
                await EnsureSubscriptionAsync(channels.Publish, cancellationToken).AnyContext();
                published = await start(channels.Publish, cancellationToken).AnyContext();
            }

            LogSubscription(channels.Send, channels.Publish, wireSent, wirePublished);
            return new MessageSubscription(sent, published);
        }
        catch
        {
            if (sent is not null)
                await sent.DisposeAsync().AnyContext();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        return _core.DisposeAsync();
    }

    // A subscription is one logical attachment listening on the type's two delivery channels: the send (queue-role)
    // destination and this subscriber's identity on the publish (topic-role) route. The publish channel is provisioned
    // before listening so a publish can reach it from the first message.
    private (ListenerConfig Send, ListenerConfig Publish) BuildChannels(MessageSubscriptionOptions? options, Type fallbackType)
    {
        options ??= new MessageSubscriptionOptions();
        if (options.PerInstance && !String.IsNullOrEmpty(options.Subscription))
            throw new ArgumentException("PerInstance and Subscription are mutually exclusive: PerInstance derives a unique per-instance subscription.", nameof(options));

        var routeType = options.RouteType ?? fallbackType;

        // The default consumer key is unique per subscription so multiple handlers can attach to the same type: they
        // compete round-robin for sent messages (a command still reaches exactly one handler instance) and each keeps
        // its own subscriber group for published ones. An explicit Key opts subscriptions into one shared group.
        string uniqueKey = Guid.NewGuid().ToString("N");

        var destination = GetDestination(routeType, options.Destination);
        var send = new ListenerConfig
        {
            Source = destination,
            Key = !String.IsNullOrEmpty(options.Key) ? options.Key : $"{destination.Key}:{uniqueKey}",
            MessageType = routeType,
            AckMode = options.AckMode,
            MaxConcurrency = options.MaxConcurrency,
            MaxAttempts = options.MaxAttempts,
            RedeliveryBackoff = options.RedeliveryBackoff,
            DeadLetterWhen = options.DeadLetterWhen
        };

        var topic = GetTopic(routeType, options.Topic);
        string subscription = options.PerInstance
            ? $"{Environment.MachineName}-{Guid.NewGuid():N}"
            : options.Subscription ?? QualifySubscription(GetSubscription(routeType, topic.Name, null), options.SubscriptionQualifier);
        var publish = new ListenerConfig
        {
            // The source is the topic-qualified subscription address, not the bare subscription name, so the same
            // subscription identity used on two topics resolves to two distinct sources (and isolates).
            Source = DestinationAddress.ForSubscription(topic.Name, subscription),
            Key = !String.IsNullOrEmpty(options.Key) ? options.Key : $"{topic.Name}:{subscription}:{uniqueKey}",
            MessageType = routeType,
            AckMode = options.AckMode,
            MaxConcurrency = options.MaxConcurrency,
            MaxAttempts = options.MaxAttempts,
            RedeliveryBackoff = options.RedeliveryBackoff,
            DeadLetterWhen = options.DeadLetterWhen
        };

        return (send, publish);
    }

    private static string QualifySubscription(string identity, string? qualifier)
    {
        return String.IsNullOrEmpty(qualifier) ? identity : $"{identity}.{MessageRoutingConventions.ToKebabCase(qualifier)}";
    }

    // Delivery semantics must never be invisible: log each subscription's effective topology (which destination it
    // consumes, which subscriber group it joins, and its retry posture) once at subscribe time.
    private void LogSubscription(ListenerConfig send, ListenerConfig publish, bool sentWired, bool publishedWired)
    {
        _logger.LogInformation(
            "Subscribed {MessageType}: send={Destination}, publish={Subscription}, concurrency={MaxConcurrency}, attempts={MaxAttempts}, ack={AckMode}",
            send.MessageType.Name, sentWired ? send.Source.Key : "(none)", publishedWired ? publish.Source.Key : "(none)", Math.Max(1, send.MaxConcurrency), send.MaxAttempts?.ToString() ?? "default", send.AckMode);
    }

    private Task EnsureTopicAsync(DestinationAddress topic, CancellationToken cancellationToken)
    {
        return _core.EnsureAsync([new DestinationDeclaration { Address = topic }], cancellationToken);
    }

    private Task EnsureSubscriptionAsync(ListenerConfig config, CancellationToken cancellationToken)
    {
        return _core.EnsureAsync([
            new DestinationDeclaration { Address = DestinationAddress.ForTopic(config.Source.Topic!) },
            new DestinationDeclaration { Address = config.Source }
        ], cancellationToken);
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

    private string GetSubscription(Type messageType, string topic, string? subscription)
    {
        return _core.Router.ResolveSubscription(new MessageSubscriptionContext
        {
            MessageType = messageType,
            Topic = topic,
            OperationOverride = subscription
        });
    }

    private static MessageEnvelopeOptions ToEnvelope(MessageSendOptions options)
    {
        return new MessageEnvelopeOptions
        {
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
            Priority = options.Priority,
            Delay = options.Delay,
            DeliverAt = options.DeliverAt,
            TimeToLive = options.TimeToLive,
            CorrelationId = options.CorrelationId,
            Headers = options.Headers
        };
    }

    private sealed class MessageSubscription : IMessageSubscription
    {
        private readonly MessageListenerHandle? _sent;
        private readonly MessageListenerHandle? _published;

        public MessageSubscription(MessageListenerHandle? sent, MessageListenerHandle? published)
        {
            _sent = sent;
            _published = published;
        }

        public string Key => (_sent ?? _published)!.Key;
        public string Destination => _sent?.Source.Key ?? "";
        public string Topic => _published?.Topic ?? "";
        public string Subscription => _published?.Subscription ?? "";
        public string Source => _published?.Source.Key ?? "";

        public async ValueTask DisposeAsync()
        {
            if (_sent is not null)
                await _sent.DisposeAsync().AnyContext();
            if (_published is not null)
                await _published.DisposeAsync().AnyContext();
        }
    }
}

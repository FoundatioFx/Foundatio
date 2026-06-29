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

public sealed record PubSubMessageOptions
{
    public MessagePriority Priority { get; init; } = MessagePriority.Normal;
    public TimeSpan? Delay { get; init; }
    public DateTimeOffset? DeliverAt { get; init; }
    public TimeSpan? TimeToLive { get; init; }
    public string? CorrelationId { get; init; }
    public string? DeduplicationId { get; init; }
    public string? Topic { get; init; }
    public MessageHeaders? Headers { get; init; }
}

public sealed record PubSubSubscriptionOptions
{
    public string? Topic { get; init; }
    public Type? RouteType { get; init; }
    public string? Subscription { get; init; }
    public string? Key { get; init; }
    public AckMode AckMode { get; init; } = AckMode.Auto;
    public int MaxConcurrency { get; init; } = 1;
    // Null falls back to the pub/sub default RetryPolicy.
    public int? MaxAttempts { get; init; }
    public Func<int, TimeSpan>? RedeliveryBackoff { get; init; }
}

public sealed record PubSubOptions
{
    public ISerializer Serializer { get; init; } = DefaultSerializer.Instance;
    public string ContentType { get; init; } = "application/json";
    public IMessageRouter Router { get; init; } = DefaultMessageRouter.Instance;
    public IJobRuntimeStore? RuntimeStore { get; init; }
    public RetryPolicy RetryPolicy { get; init; } = new();
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public ILoggerFactory? LoggerFactory { get; init; }
}

public interface IPubSub : IAsyncDisposable
{
    Task PublishAsync<T>(T message, PubSubMessageOptions? options = null, CancellationToken cancellationToken = default) where T : class;
    Task PublishBatchAsync<T>(IEnumerable<T> messages, PubSubMessageOptions? options = null, CancellationToken cancellationToken = default) where T : class;
    Task PublishBatchAsync(IEnumerable<object> messages, PubSubMessageOptions? options = null, CancellationToken cancellationToken = default);
    Task<IMessageSubscription> SubscribeAsync(Func<IReceivedMessage, CancellationToken, Task> handler, PubSubSubscriptionOptions? options = null, CancellationToken cancellationToken = default);
    Task<IMessageSubscription> SubscribeAsync<T>(Func<IReceivedMessage<T>, CancellationToken, Task> handler, PubSubSubscriptionOptions? options = null, CancellationToken cancellationToken = default) where T : class;
    Task RunSubscriptionAsync(Func<IReceivedMessage, CancellationToken, Task> handler, PubSubSubscriptionOptions? options = null, CancellationToken cancellationToken = default);
    Task RunSubscriptionAsync<T>(Func<IReceivedMessage<T>, CancellationToken, Task> handler, PubSubSubscriptionOptions? options = null, CancellationToken cancellationToken = default) where T : class;
}

public interface IMessageSubscription : IAsyncDisposable
{
    string Topic { get; }
    string Subscription { get; }
    string Key { get; }
}

/// <summary>
/// App-facing fan-out pub/sub. Routing, serialization, settlement, scheduling, and the subscription loop live in
/// <see cref="MessageClientCore"/>; this type maps topic/subscription-shaped options onto that shared core.
/// </summary>
public sealed class PubSub : IPubSub
{
    private readonly MessageClientCore _core;

    public PubSub(IMessageTransport transport, PubSubOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        options ??= new PubSubOptions();
        var logger = (options.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger<PubSub>();
        _core = new MessageClientCore(transport, options.Serializer, options.Router, options.RuntimeStore, options.TimeProvider, logger,
            static (message, inner) => inner is null ? new MessageBusException(message) : new MessageBusException(message, inner), options.RetryPolicy);
    }

    public Task PublishAsync<T>(T message, PubSubMessageOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(message);
        options ??= new PubSubMessageOptions();
        return _core.SendAsync(ScheduledDispatchKind.PubSubMessage, typeof(T), message, ToEnvelope(options), GetTopic(typeof(T), options.Topic), EnsureTopicAsync, cancellationToken);
    }

    public Task PublishBatchAsync<T>(IEnumerable<T> messages, PubSubMessageOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(messages);
        options ??= new PubSubMessageOptions();
        return _core.SendBatchAsync(ScheduledDispatchKind.PubSubMessage, messages.Cast<object>(), typeof(T), ToEnvelope(options), type => GetTopic(type, options.Topic), EnsureTopicAsync, cancellationToken);
    }

    public Task PublishBatchAsync(IEnumerable<object> messages, PubSubMessageOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        options ??= new PubSubMessageOptions();
        return _core.SendBatchAsync(ScheduledDispatchKind.PubSubMessage, messages, null, ToEnvelope(options), type => GetTopic(type, options.Topic), EnsureTopicAsync, cancellationToken);
    }

    public async Task<IMessageSubscription> SubscribeAsync(Func<IReceivedMessage, CancellationToken, Task> handler, PubSubSubscriptionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        options ??= new PubSubSubscriptionOptions();
        var config = BuildConfig(options.RouteType ?? typeof(object), options);
        await EnsureSubscriptionAsync(config, cancellationToken).AnyContext();
        return await _core.StartListenerAsync(config, handler, cancellationToken).AnyContext();
    }

    public async Task<IMessageSubscription> SubscribeAsync<T>(Func<IReceivedMessage<T>, CancellationToken, Task> handler, PubSubSubscriptionOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        options ??= new PubSubSubscriptionOptions();
        var config = BuildConfig(options.RouteType ?? typeof(T), options);
        await EnsureSubscriptionAsync(config, cancellationToken).AnyContext();
        return await _core.StartListenerAsync(config, handler, cancellationToken).AnyContext();
    }

    public async Task RunSubscriptionAsync(Func<IReceivedMessage, CancellationToken, Task> handler, PubSubSubscriptionOptions? options = null, CancellationToken cancellationToken = default)
    {
        await using var subscription = await SubscribeAsync(handler, options, cancellationToken).AnyContext();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).AnyContext();
    }

    public async Task RunSubscriptionAsync<T>(Func<IReceivedMessage<T>, CancellationToken, Task> handler, PubSubSubscriptionOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        await using var subscription = await SubscribeAsync(handler, options, cancellationToken).AnyContext();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).AnyContext();
    }

    public ValueTask DisposeAsync()
    {
        return _core.DisposeAsync();
    }

    private ListenerConfig BuildConfig(Type routeType, PubSubSubscriptionOptions options)
    {
        string topic = GetTopic(routeType, options.Topic);
        string subscription = GetSubscription(routeType, topic, options.Subscription);
        return new ListenerConfig
        {
            Topic = topic,
            Subscription = subscription,
            Source = subscription, // a pub/sub consumer receives from its subscription destination
            Key = !String.IsNullOrEmpty(options.Key) ? options.Key : $"{topic}:{subscription}:{routeType.FullName ?? routeType.Name}",
            MessageType = routeType,
            AckMode = options.AckMode,
            MaxConcurrency = options.MaxConcurrency,
            MaxAttempts = options.MaxAttempts,
            RedeliveryBackoff = options.RedeliveryBackoff
        };
    }

    private Task EnsureTopicAsync(string topic, CancellationToken cancellationToken)
    {
        return _core.EnsureAsync([new DestinationDeclaration { Name = topic, Role = DestinationRole.Topic }], cancellationToken);
    }

    private Task EnsureSubscriptionAsync(ListenerConfig config, CancellationToken cancellationToken)
    {
        return _core.EnsureAsync([
            new DestinationDeclaration { Name = config.Topic, Role = DestinationRole.Topic },
            new DestinationDeclaration { Name = config.Subscription, Role = DestinationRole.Subscription, Source = config.Topic }
        ], cancellationToken);
    }

    private string GetTopic(Type messageType, string? topic)
    {
        return _core.Router.ResolveRoute(new MessageRouteContext
        {
            MessageType = messageType,
            Role = MessageRouteRole.PubSubTopic,
            OperationOverride = topic
        });
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

    private static MessageEnvelopeOptions ToEnvelope(PubSubMessageOptions options)
    {
        return new MessageEnvelopeOptions
        {
            Priority = options.Priority,
            Delay = options.Delay,
            DeliverAt = options.DeliverAt,
            TimeToLive = options.TimeToLive,
            CorrelationId = options.CorrelationId,
            DeduplicationId = options.DeduplicationId,
            Headers = options.Headers
        };
    }
}

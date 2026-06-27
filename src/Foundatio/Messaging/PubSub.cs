using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Serializer;
using Foundatio.Utility;

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
    public string? Subscription { get; init; }
    public string? Key { get; init; }
    public AckMode AckMode { get; init; } = AckMode.Auto;
    public int MaxConcurrency { get; init; } = 1;
    public int MaxAttempts { get; init; } = 5;
}

public sealed record PubSubOptions
{
    public ISerializer Serializer { get; init; } = DefaultSerializer.Instance;
    public string ContentType { get; init; } = "application/json";
    public Func<Type, string>? TopicResolver { get; init; }
    public Func<Type, string>? MessageTypeResolver { get; init; }
    public Func<Type, string, string>? SubscriptionResolver { get; init; }
    public IJobRuntimeStore? RuntimeStore { get; init; }
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

public interface IPubSub : IAsyncDisposable
{
    Task PublishAsync<T>(T message, PubSubMessageOptions? options = null, CancellationToken cancellationToken = default) where T : class;
    Task PublishBatchAsync<T>(IEnumerable<T> messages, PubSubMessageOptions? options = null, CancellationToken cancellationToken = default) where T : class;
    Task<IMessageSubscription> SubscribeAsync<T>(Func<IReceivedMessage<T>, CancellationToken, Task> handler, PubSubSubscriptionOptions? options = null, CancellationToken cancellationToken = default) where T : class;
    Task RunSubscriptionAsync<T>(Func<IReceivedMessage<T>, CancellationToken, Task> handler, PubSubSubscriptionOptions? options = null, CancellationToken cancellationToken = default) where T : class;
}

public interface IMessageSubscription : IAsyncDisposable
{
    string Topic { get; }
    string Subscription { get; }
    string Key { get; }
}

public sealed class PubSub : IPubSub
{
    private readonly IMessageTransport _transport;
    private readonly PubSubOptions _options;
    private readonly ConcurrentDictionary<string, MessageSubscriptionHandle> _subscriptions = new(StringComparer.Ordinal);
    private int _isDisposed;

    public PubSub(IMessageTransport transport, PubSubOptions? options = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? new PubSubOptions();
    }

    public async Task PublishAsync<T>(T message, PubSubMessageOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();

        options ??= new PubSubMessageOptions();
        ValidateSendOptions(options);

        string topic = GetTopic(typeof(T), options.Topic);
        await EnsureTopicAsync(topic, cancellationToken).AnyContext();

        var sendOptions = CreateSendOptions(options);
        string messageId = options.DeduplicationId ?? Guid.NewGuid().ToString("N");
        var transportMessage = CreateTransportMessage(message, options, messageId);

        if (await TryScheduleDispatchAsync(topic, transportMessage, sendOptions, cancellationToken).AnyContext())
            return;

        var result = await _transport.SendAsync(topic, [transportMessage], sendOptions, cancellationToken).AnyContext();
        var item = result.Items.Count > 0 ? result.Items[0] : null;
        if (item is null || !item.Success)
            throw new MessageBusException($"Unable to publish message to \"{topic}\": {item?.ErrorCode ?? "unknown error"}");
    }

    public async Task PublishBatchAsync<T>(IEnumerable<T> messages, PubSubMessageOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(messages);
        ThrowIfDisposed();

        options ??= new PubSubMessageOptions();
        ValidateSendOptions(options);

        string topic = GetTopic(typeof(T), options.Topic);
        await EnsureTopicAsync(topic, cancellationToken).AnyContext();

        var sendOptions = CreateSendOptions(options);
        int index = 0;
        var transportMessages = messages.Select(message =>
        {
            ArgumentNullException.ThrowIfNull(message);
            string? messageId = options.DeduplicationId is null ? null : $"{options.DeduplicationId}:{index}";
            index++;
            return CreateTransportMessage(message, options, messageId);
        }).ToArray();

        if (transportMessages.Length == 0)
            return;

        if (await TryScheduleDispatchesAsync(topic, transportMessages, sendOptions, cancellationToken).AnyContext())
            return;

        var result = await _transport.SendAsync(topic, transportMessages, sendOptions, cancellationToken).AnyContext();
        if (!result.AllSucceeded)
            throw new MessageBusException($"Unable to publish {result.Items.Count(i => !i.Success)} of {result.Items.Count} messages to \"{topic}\".");
    }

    public async Task<IMessageSubscription> SubscribeAsync<T>(Func<IReceivedMessage<T>, CancellationToken, Task> handler, PubSubSubscriptionOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        options ??= new PubSubSubscriptionOptions();
        string topic = GetTopic(typeof(T), options.Topic);
        string subscription = GetSubscription(typeof(T), topic, options.Subscription);
        string key = GetSubscriptionKey(typeof(T), topic, subscription, options.Key);
        await EnsureSubscriptionAsync(topic, subscription, cancellationToken).AnyContext();

        if (_subscriptions.TryGetValue(key, out var existing) && !existing.IsDisposed)
            return existing;

        var handle = new MessageSubscriptionHandle(topic, subscription, key, RemoveSubscription);
        if (!_subscriptions.TryAdd(key, handle))
        {
            await handle.DisposeAsync().AnyContext();
            return _subscriptions[key];
        }

        try
        {
            if (_transport is ISupportsPush push)
            {
                var pushSubscription = await push.SubscribeAsync(subscription, async (entry, token) =>
                {
                    var received = await CreateReceivedMessageAsync<T>(entry, token).AnyContext();
                    await HandleMessageAsync(received, handler, options, token).AnyContext();
                }, new PushOptions { MaxConcurrentMessages = Math.Max(1, options.MaxConcurrency) }, cancellationToken).AnyContext();

                handle.SetPushSubscription(pushSubscription);
                return handle;
            }

            if (_transport is not ISupportsPull pull)
                throw new MessageBusException($"Transport \"{_transport.GetType().Name}\" does not support subscriptions.");

            handle.Start(RunPullSubscriptionLoopAsync(subscription, pull, handler, options, handle.CancellationToken));
            return handle;
        }
        catch
        {
            await handle.DisposeAsync().AnyContext();
            throw;
        }
    }

    public async Task RunSubscriptionAsync<T>(Func<IReceivedMessage<T>, CancellationToken, Task> handler, PubSubSubscriptionOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        await using var subscription = await SubscribeAsync(handler, options, cancellationToken).AnyContext();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).AnyContext();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
            return;

        var subscriptions = _subscriptions.Values.ToArray();
        foreach (var subscription in subscriptions)
            await subscription.DisposeAsync().AnyContext();

        await _transport.DisposeAsync().AnyContext();
    }

    private async Task RunPullSubscriptionLoopAsync<T>(string subscription, ISupportsPull pull, Func<IReceivedMessage<T>, CancellationToken, Task> handler, PubSubSubscriptionOptions options, CancellationToken cancellationToken) where T : class
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var entries = await pull.ReceiveAsync(subscription, new ReceiveRequest
            {
                MaxMessages = Math.Max(1, options.MaxConcurrency),
                MaxWaitTime = TimeSpan.FromSeconds(1)
            }, cancellationToken).AnyContext();

            var tasks = entries.Select(async entry =>
            {
                var received = await CreateReceivedMessageAsync<T>(entry, cancellationToken).AnyContext();
                await HandleMessageAsync(received, handler, options, cancellationToken).AnyContext();
            }).ToArray();

            await Task.WhenAll(tasks).AnyContext();
        }
    }

    private async Task EnsureTopicAsync(string topic, CancellationToken cancellationToken)
    {
        if (_transport is ISupportsProvisioning provisioning)
            await provisioning.EnsureAsync([new DestinationDeclaration { Name = topic, Role = DestinationRole.Topic }], cancellationToken).AnyContext();
    }

    private async Task EnsureSubscriptionAsync(string topic, string subscription, CancellationToken cancellationToken)
    {
        if (_transport is ISupportsProvisioning provisioning)
        {
            await provisioning.EnsureAsync([
                new DestinationDeclaration { Name = topic, Role = DestinationRole.Topic },
                new DestinationDeclaration { Name = subscription, Role = DestinationRole.Subscription, Source = topic }
            ], cancellationToken).AnyContext();
        }
    }

    private async Task<IReceivedMessage<T>> CreateReceivedMessageAsync<T>(TransportEntry entry, CancellationToken cancellationToken) where T : class
    {
        try
        {
            var message = _options.Serializer.Deserialize<T>(entry.Body);
            if (message is null)
                throw new MessageBusException($"Message \"{entry.Id}\" deserialized to null.");

            return new ReceivedMessage<T>(_transport, entry, message, cancellationToken, _options.RuntimeStore, _options.TimeProvider);
        }
        catch (Exception ex) when (ex is not MessageBusException)
        {
            await DeadLetterPoisonMessageAsync(entry, "deserialize-failure", cancellationToken).AnyContext();
            throw new MessageBusException($"Unable to deserialize message \"{entry.Id}\".", ex);
        }
        catch (MessageBusException)
        {
            await DeadLetterPoisonMessageAsync(entry, "deserialize-failure", cancellationToken).AnyContext();
            throw;
        }
    }

    private async Task HandleMessageAsync<T>(IReceivedMessage<T> message, Func<IReceivedMessage<T>, CancellationToken, Task> handler, PubSubSubscriptionOptions options, CancellationToken cancellationToken) where T : class
    {
        try
        {
            await handler(message, cancellationToken).AnyContext();

            if (options.AckMode == AckMode.Auto && !message.IsHandled)
                await message.CompleteAsync(cancellationToken).AnyContext();
        }
        catch
        {
            if (message.IsHandled)
                return;

            if (message.Attempts >= options.MaxAttempts)
            {
                await message.DeadLetterAsync("handler-error", cancellationToken).AnyContext();
                return;
            }

            await message.AbandonAsync(cancellationToken).AnyContext();
        }
    }

    private TransportMessage CreateTransportMessage<T>(T message, PubSubMessageOptions options, string? messageId = null) where T : class
    {
        var headers = (options.Headers ?? MessageHeaders.Empty).ToBuilder()
            .Set(KnownHeaders.MessageType, GetMessageType(typeof(T)))
            .Set(KnownHeaders.ContentType, _options.ContentType)
            .Set(KnownHeaders.Priority, options.Priority.ToString());

        if (!String.IsNullOrEmpty(options.CorrelationId))
            headers.Set(KnownHeaders.CorrelationId, options.CorrelationId);

        if (Activity.Current is { } activity)
        {
            if (!String.IsNullOrEmpty(activity.Id))
                headers.SetIfMissing(KnownHeaders.TraceParent, activity.Id);

            if (!String.IsNullOrEmpty(activity.TraceStateString))
                headers.SetIfMissing(KnownHeaders.TraceState, activity.TraceStateString);
        }

        if (options.TimeToLive is { } ttl)
            headers.Set(KnownHeaders.Expiration, _options.TimeProvider.GetUtcNow().Add(ttl).ToString("O", CultureInfo.InvariantCulture));

        return new TransportMessage
        {
            Body = _options.Serializer.SerializeToBytes(message),
            Headers = headers.Build(),
            MessageId = messageId
        };
    }

    private TransportSendOptions CreateSendOptions(PubSubMessageOptions options)
    {
        return new TransportSendOptions
        {
            Priority = options.Priority,
            DeliverAt = options.DeliverAt ?? (options.Delay is { } delay ? _options.TimeProvider.GetUtcNow().Add(delay) : null),
            DeduplicationId = options.DeduplicationId
        };
    }

    private void ValidateSendOptions(PubSubMessageOptions options)
    {
        if (options.Priority != MessagePriority.Normal && _transport is not ISupportsPriority)
            throw new NotSupportedException($"Transport \"{_transport.GetType().Name}\" does not support message priority.");

        if (options.TimeToLive is not null && _transport is not ISupportsExpiration)
            throw new NotSupportedException($"Transport \"{_transport.GetType().Name}\" does not support message expiration.");
    }

    private async Task<bool> TryScheduleDispatchesAsync(string topic, IReadOnlyList<TransportMessage> messages, TransportSendOptions options, CancellationToken cancellationToken)
    {
        if (!ShouldScheduleThroughRuntimeStore(options, out var dueUtc))
            return false;

        for (int index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            string messageId = message.MessageId ?? Guid.NewGuid().ToString("N");
            await ScheduleDispatchAsync(topic, message with { MessageId = messageId }, options, dueUtc, cancellationToken).AnyContext();
        }

        return true;
    }

    private Task<bool> TryScheduleDispatchAsync(string topic, TransportMessage message, TransportSendOptions options, CancellationToken cancellationToken)
    {
        return TryScheduleDispatchesAsync(topic, [message], options, cancellationToken);
    }

    private bool ShouldScheduleThroughRuntimeStore(TransportSendOptions options, out DateTimeOffset dueUtc)
    {
        dueUtc = options.DeliverAt.GetValueOrDefault();
        if (options.DeliverAt is null || dueUtc <= _options.TimeProvider.GetUtcNow())
            return false;

        if (_transport is ISupportsDelayedDelivery)
            return false;

        if (_options.RuntimeStore is null)
            throw new MessageBusException($"Delayed publish requires either native delayed-delivery support from transport \"{_transport.GetType().Name}\" or {nameof(PubSubOptions)}.{nameof(PubSubOptions.RuntimeStore)}.");

        return true;
    }

    private Task ScheduleDispatchAsync(string topic, TransportMessage message, TransportSendOptions options, DateTimeOffset dueUtc, CancellationToken cancellationToken)
    {
        return _options.RuntimeStore!.ScheduleDispatchAsync(new ScheduledDispatchState
        {
            DispatchId = message.MessageId!,
            Kind = ScheduledDispatchKind.PubSubMessage,
            Destination = topic,
            Body = message.Body,
            Headers = message.Headers,
            Options = options with { DeliverAt = null },
            DueUtc = dueUtc
        }, cancellationToken);
    }

    private string GetTopic(Type messageType, string? topic)
    {
        if (!String.IsNullOrEmpty(topic))
            return topic;

        if (_options.TopicResolver?.Invoke(messageType) is { Length: > 0 } resolved)
            return resolved;

        var route = messageType.GetCustomAttribute<MessageRouteAttribute>();
        return route?.Topic ?? route?.Destination ?? MessageRoutingConventions.ToKebabCase(messageType.Name);
    }

    private string GetSubscription(Type messageType, string topic, string? subscription)
    {
        if (!String.IsNullOrEmpty(subscription))
            return subscription;

        if (_options.SubscriptionResolver?.Invoke(messageType, topic) is { Length: > 0 } resolved)
            return resolved;

        return messageType.GetCustomAttribute<MessageRouteAttribute>()?.Subscription ?? $"{topic}.{MessageRoutingConventions.ToKebabCase(messageType.Name)}";
    }

    private static string GetSubscriptionKey(Type messageType, string topic, string subscription, string? key)
    {
        return !String.IsNullOrEmpty(key)
            ? key
            : $"{topic}:{subscription}:{messageType.FullName ?? messageType.Name}";
    }

    private string GetMessageType(Type messageType)
    {
        return _options.MessageTypeResolver?.Invoke(messageType) ?? messageType.FullName ?? messageType.Name;
    }

    private async Task DeadLetterPoisonMessageAsync(TransportEntry entry, string reason, CancellationToken cancellationToken)
    {
        await ReceivedMessage<object>.DeadLetterAsync(_transport, entry, reason, cancellationToken).AnyContext();
    }

    private void RemoveSubscription(string key, MessageSubscriptionHandle handle)
    {
        _subscriptions.TryRemove(new KeyValuePair<string, MessageSubscriptionHandle>(key, handle));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) == 1, this);
    }
}

internal sealed class MessageSubscriptionHandle : IMessageSubscription
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Action<string, MessageSubscriptionHandle> _remove;
    private IPushSubscription? _pushSubscription;
    private Task? _worker;
    private int _isDisposed;

    public MessageSubscriptionHandle(string topic, string subscription, string key, Action<string, MessageSubscriptionHandle> remove)
    {
        Topic = topic;
        Subscription = subscription;
        Key = key;
        _remove = remove;
    }

    public string Topic { get; }
    public string Subscription { get; }
    public string Key { get; }
    public CancellationToken CancellationToken => _cancellationTokenSource.Token;
    public bool IsDisposed => Volatile.Read(ref _isDisposed) == 1;

    public void SetPushSubscription(IPushSubscription subscription)
    {
        _pushSubscription = subscription;
    }

    public void Start(Task worker)
    {
        _worker = worker;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
            return;

        await _cancellationTokenSource.CancelAsync().AnyContext();

        if (_pushSubscription is not null)
            await _pushSubscription.DisposeAsync().AnyContext();

        if (_worker is not null)
        {
            try
            {
                await _worker.AnyContext();
            }
            catch (OperationCanceledException) { }
        }

        _cancellationTokenSource.Dispose();
        _remove(Key, this);
    }
}

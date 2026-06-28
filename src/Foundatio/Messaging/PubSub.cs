using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
    public int MaxAttempts { get; init; } = 5;
}

public sealed record PubSubOptions
{
    public ISerializer Serializer { get; init; } = DefaultSerializer.Instance;
    public string ContentType { get; init; } = "application/json";
    public IMessageRouter Router { get; init; } = DefaultMessageRouter.Instance;
    public IJobRuntimeStore? RuntimeStore { get; init; }
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

public sealed class PubSub : IPubSub
{
    private readonly IMessageTransport _transport;
    private readonly PubSubOptions _options;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, MessageSubscriptionHandle> _subscriptions = new(StringComparer.Ordinal);
    private int _isDisposed;

    public PubSub(IMessageTransport transport, PubSubOptions? options = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? new PubSubOptions();
        _logger = (_options.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger<PubSub>();
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
        var transportMessage = CreateTransportMessage(message, typeof(T), options, messageId);

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
        await PublishBatchCoreAsync(messages.Cast<object>(), typeof(T), options, cancellationToken).AnyContext();
    }

    public async Task PublishBatchAsync(IEnumerable<object> messages, PubSubMessageOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        await PublishBatchCoreAsync(messages, null, options, cancellationToken).AnyContext();
    }

    public async Task<IMessageSubscription> SubscribeAsync(Func<IReceivedMessage, CancellationToken, Task> handler, PubSubSubscriptionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        options ??= new PubSubSubscriptionOptions();
        Type routeType = options.RouteType ?? typeof(object);
        string topic = GetTopic(routeType, options.Topic);
        string subscription = GetSubscription(routeType, topic, options.Subscription);
        string key = GetSubscriptionKey(routeType, topic, subscription, options.Key);
        var registration = MessageListenerRegistration.Create(handler, routeType, topic, subscription, options);
        await EnsureSubscriptionAsync(topic, subscription, cancellationToken).AnyContext();

        return await SubscribeCoreAsync(topic, subscription, key, registration, options, async (entry, token) =>
        {
            var received = CreateReceivedMessage(entry, token);
            await HandleMessageAsync(received, handler, options, token).AnyContext();
        }, cancellationToken).AnyContext();
    }

    public async Task<IMessageSubscription> SubscribeAsync<T>(Func<IReceivedMessage<T>, CancellationToken, Task> handler, PubSubSubscriptionOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        options ??= new PubSubSubscriptionOptions();
        Type routeType = options.RouteType ?? typeof(T);
        string topic = GetTopic(routeType, options.Topic);
        string subscription = GetSubscription(routeType, topic, options.Subscription);
        string key = GetSubscriptionKey(routeType, topic, subscription, options.Key);
        var registration = MessageListenerRegistration.Create(handler, routeType, topic, subscription, options);
        await EnsureSubscriptionAsync(topic, subscription, cancellationToken).AnyContext();

        return await SubscribeCoreAsync(topic, subscription, key, registration, options, async (entry, token) =>
        {
            var received = await CreateReceivedMessageAsync<T>(entry, token).AnyContext();
            await HandleMessageAsync(received, handler, options, token).AnyContext();
        }, cancellationToken).AnyContext();
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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
            return;

        var subscriptions = _subscriptions.Values.ToArray();
        foreach (var subscription in subscriptions)
            await subscription.DisposeAsync().AnyContext();

        await _transport.DisposeAsync().AnyContext();
    }

    private async Task PublishBatchCoreAsync(IEnumerable<object> messages, Type? declaredType, PubSubMessageOptions? options, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        options ??= new PubSubMessageOptions();
        ValidateSendOptions(options);

        var sendOptions = CreateSendOptions(options);
        var grouped = new Dictionary<string, List<TransportMessage>>(StringComparer.Ordinal);
        int index = 0;

        foreach (var message in messages)
        {
            ArgumentNullException.ThrowIfNull(message);
            Type messageType = declaredType ?? message.GetType();
            string topic = GetTopic(messageType, options.Topic);
            string? messageId = options.DeduplicationId is null ? null : $"{options.DeduplicationId}:{index}";
            index++;

            if (!grouped.TryGetValue(topic, out var transportMessages))
            {
                transportMessages = [];
                grouped.Add(topic, transportMessages);
            }

            transportMessages.Add(CreateTransportMessage(message, messageType, options, messageId));
        }

        foreach (var group in grouped)
        {
            await EnsureTopicAsync(group.Key, cancellationToken).AnyContext();

            if (await TryScheduleDispatchesAsync(group.Key, group.Value, sendOptions, cancellationToken).AnyContext())
                continue;

            var result = await _transport.SendAsync(group.Key, group.Value, sendOptions, cancellationToken).AnyContext();
            if (!result.AllSucceeded)
                throw new MessageBusException($"Unable to publish {result.Items.Count(i => !i.Success)} of {result.Items.Count} messages to \"{group.Key}\".");
        }
    }

    private async Task<IMessageSubscription> SubscribeCoreAsync(string topic, string subscription, string key, MessageListenerRegistration registration, PubSubSubscriptionOptions options, Func<TransportEntry, CancellationToken, Task> onMessage, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (_subscriptions.TryGetValue(key, out var existing) && !existing.IsDisposed)
        {
            existing.ThrowIfConflicting(registration);
            return existing;
        }

        var handle = new MessageSubscriptionHandle(topic, subscription, key, registration, RemoveSubscription);
        if (!_subscriptions.TryAdd(key, handle))
        {
            await handle.DisposeAsync().AnyContext();
            var current = _subscriptions[key];
            current.ThrowIfConflicting(registration);
            return current;
        }

        try
        {
            if (_transport is ISupportsPush push)
            {
                var pushSubscription = await push.SubscribeAsync(subscription, onMessage, new PushOptions { MaxConcurrentMessages = Math.Max(1, options.MaxConcurrency) }, cancellationToken).AnyContext();

                handle.SetPushSubscription(pushSubscription);
                return handle;
            }

            if (_transport is not ISupportsPull pull)
                throw new MessageBusException($"Transport \"{_transport.GetType().Name}\" does not support subscriptions.");

            handle.Start(RunPullSubscriptionLoopAsync(subscription, pull, onMessage, options, handle.CancellationToken));
            return handle;
        }
        catch
        {
            await handle.DisposeAsync().AnyContext();
            throw;
        }
    }

    // MaxConcurrency bounds the number of in-flight messages processed per receive batch. A failure while receiving
    // or while processing a single entry (including a poison message that was already dead-lettered) must never tear
    // down the subscription loop, otherwise one bad message or a transient transport blip silently stops delivery.
    private async Task RunPullSubscriptionLoopAsync(string subscription, ISupportsPull pull, Func<TransportEntry, CancellationToken, Task> onMessage, PubSubSubscriptionOptions options, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            IReadOnlyList<TransportEntry> entries;
            try
            {
                entries = await pull.ReceiveAsync(subscription, new ReceiveRequest
                {
                    MaxMessages = Math.Max(1, options.MaxConcurrency),
                    MaxWaitTime = TimeSpan.FromSeconds(1)
                }, cancellationToken).AnyContext();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error receiving from subscription \"{Subscription}\"; retrying: {Message}", subscription, ex.Message);
                await _options.TimeProvider.SafeDelay(TimeSpan.FromSeconds(1), cancellationToken).AnyContext();
                continue;
            }

            var tasks = entries.Select(entry => SafeProcessAsync(entry, onMessage, subscription, cancellationToken)).ToArray();
            await Task.WhenAll(tasks).AnyContext();
        }
    }

    private async Task SafeProcessAsync(TransportEntry entry, Func<TransportEntry, CancellationToken, Task> onMessage, string subscription, CancellationToken cancellationToken)
    {
        try
        {
            await onMessage(entry, cancellationToken).AnyContext();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message \"{MessageId}\" from subscription \"{Subscription}\": {Message}", entry.Id, subscription, ex.Message);
        }
    }

    private ReceivedMessage CreateReceivedMessage(TransportEntry entry, CancellationToken cancellationToken)
    {
        return new ReceivedMessage(_transport, entry, cancellationToken, _options.RuntimeStore, _options.TimeProvider);
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

    private async Task HandleMessageAsync(IReceivedMessage message, Func<IReceivedMessage, CancellationToken, Task> handler, PubSubSubscriptionOptions options, CancellationToken cancellationToken)
    {
        try
        {
            await handler(message, cancellationToken).AnyContext();

            if (options.AckMode == AckMode.Auto && !message.IsHandled)
                await message.CompleteAsync(cancellationToken).AnyContext();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Subscriber failed for message \"{MessageId}\" from \"{Subscription}\" (attempt {Attempt} of {MaxAttempts}): {Message}", message.Id, message.MessageType, message.Attempts, options.MaxAttempts, ex.Message);
            await SettleFailedMessageAsync(message, options, cancellationToken).AnyContext();
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Subscriber failed for message \"{MessageId}\" from \"{Subscription}\" (attempt {Attempt} of {MaxAttempts}): {Message}", message.Id, message.MessageType, message.Attempts, options.MaxAttempts, ex.Message);
            await SettleFailedMessageAsync(message, options, cancellationToken).AnyContext();
        }
    }

    private static async Task SettleFailedMessageAsync(IReceivedMessage message, PubSubSubscriptionOptions options, CancellationToken cancellationToken)
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

    private TransportMessage CreateTransportMessage(object message, Type messageType, PubSubMessageOptions options, string? messageId = null)
    {
        // Content type is intentionally not written as a header: the receive path always uses the single configured
        // serializer, so advertising a per-message content type would be misleading until real negotiation exists.
        var headers = (options.Headers ?? MessageHeaders.Empty).ToBuilder()
            .Set(KnownHeaders.MessageType, GetMessageType(messageType))
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
        return _options.Router.ResolveRoute(new MessageRouteContext
        {
            MessageType = messageType,
            Role = MessageRouteRole.PubSubTopic,
            OperationOverride = topic
        });
    }

    private string GetSubscription(Type messageType, string topic, string? subscription)
    {
        return _options.Router.ResolveSubscription(new MessageSubscriptionContext
        {
            MessageType = messageType,
            Topic = topic,
            OperationOverride = subscription
        });
    }

    private static string GetSubscriptionKey(Type messageType, string topic, string subscription, string? key)
    {
        return !String.IsNullOrEmpty(key)
            ? key
            : $"{topic}:{subscription}:{messageType.FullName ?? messageType.Name}";
    }

    private string GetMessageType(Type messageType)
    {
        return _options.Router.ResolveMessageType(messageType);
    }

    private async Task DeadLetterPoisonMessageAsync(TransportEntry entry, string reason, CancellationToken cancellationToken)
    {
        await ReceivedMessage.DeadLetterAsync(_transport, entry, reason, cancellationToken).AnyContext();
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

    public MessageSubscriptionHandle(string topic, string subscription, string key, MessageListenerRegistration registration, Action<string, MessageSubscriptionHandle> remove)
    {
        Topic = topic;
        Subscription = subscription;
        Key = key;
        Registration = registration;
        _remove = remove;
    }

    public string Topic { get; }
    public string Subscription { get; }
    public string Key { get; }
    public MessageListenerRegistration Registration { get; }
    public CancellationToken CancellationToken => _cancellationTokenSource.Token;
    public bool IsDisposed => Volatile.Read(ref _isDisposed) == 1;

    public void ThrowIfConflicting(MessageListenerRegistration registration)
    {
        if (!Registration.Matches(registration))
            throw new InvalidOperationException($"A subscription with key \"{Key}\" is already registered with different handler or options.");
    }

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

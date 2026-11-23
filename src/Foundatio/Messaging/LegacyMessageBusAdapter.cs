using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging.Transport;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging;

/// <summary>
/// Implements the original <see cref="IMessageBus"/> on top of an <see cref="IMessageTransport"/>, so existing code
/// (including <c>HybridCacheClient</c> and <c>CacheLockProvider</c>) runs unchanged on any new transport.
/// </summary>
/// <remarks>
/// Semantics match the original buses: every running instance receives every message once, subscribers also receive
/// derived types, delivery is best effort, and handler exceptions are logged rather than retried. Messages are
/// received by a <see cref="MessageProcessor"/> in batches of up to <see cref="MaxBatchSize"/>, so a failed receiver
/// is replaced and leases are renewed while subscribers run. They are handled one at a time in arrival order, each
/// completed while the next is handled, and a slow subscriber does not hold up the other subscribers of a message.
/// <para>
/// Created by <c>AddLegacyMessageBus()</c>, it also shares the message types <see cref="IMessageClient"/> knows (mapped,
/// marked with <see cref="MessageAttribute"/>, or handled by a registered handler): publishing one also sends a copy,
/// with the headers <see cref="IMessageClient.PublishAsync{T}"/> adds, to its topic, and subscribing to one also opens
/// an instance subscription to that topic, so its subscribers receive what <see cref="IMessageClient"/> publishes. A
/// copy keeps the legacy type header, which tells that subscription to skip it: its subscribers already received it
/// from the legacy topic. So each subscriber, on either API, gets every publish once.
/// </para>
/// <para>
/// A publish of one of <see cref="LegacyMessageBusOptions.MigratedMessageTypes"/> is sent only to its topic, without
/// the legacy type header, exactly as <see cref="IMessageClient.PublishAsync{T}"/> would send it, and subscribers of it,
/// its base types or <see cref="IMessage"/> receive it from there.
/// </para>
/// </remarks>
internal sealed class LegacyMessageBusAdapter : IMessageBus
{
    private const string LegacyTypeHeader = "legacy-type";
    private const int MaxCachedTypeMisses = 1000;

    /// <summary>The most messages received at once, and so held in flight, when the transport allows that many.</summary>
    internal const int MaxBatchSize = 100;

    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ReplaceTimeout = TimeSpan.FromSeconds(5);

    private readonly IMessageTransport _transport;
    private readonly MessagingRuntime? _runtime;
    private readonly ISerializer _serializer;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly string _topic;
    private readonly MessageDestination _destination;
    private readonly IReadOnlyDictionary<string, Type> _typeMappings;
    private readonly HashSet<Type> _migratedTypes;
    private readonly ConcurrentDictionary<Type, string> _typeNames = new();
    private readonly ConcurrentDictionary<Guid, Subscriber> _subscribers = new();
    private readonly ConcurrentDictionary<string, Type?> _types = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Type, bool> _attributedTypes = new();
    private readonly Dictionary<string, MessageProcessor> _sharedTopics = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly CancellationTokenSource _disposed = new();
    private MessageProcessor? _processor;
    private volatile MessageProcessor[] _processors = [];
    private int _typeMisses;

    /// <summary>A legacy bus on <paramref name="transport"/> that shares nothing with <see cref="IMessageClient"/>.</summary>
    public LegacyMessageBusAdapter(IMessageTransport transport, SharedMessageBusOptions options) : this(transport, null, options)
    {
    }

    /// <summary>A legacy bus on <paramref name="runtime"/>'s transport that shares the message types it knows with <see cref="IMessageClient"/>.</summary>
    /// <exception cref="ArgumentException"><see cref="LegacyMessageBusOptions.MigratedMessageTypes"/> has a null entry.</exception>
    public LegacyMessageBusAdapter(MessagingRuntime runtime, SharedMessageBusOptions options) : this(runtime.Transport, runtime, options)
    {
        runtime.LegacyBus = this;
    }

    private LegacyMessageBusAdapter(IMessageTransport transport, MessagingRuntime? runtime, SharedMessageBusOptions options)
    {
        _transport = transport;
        _runtime = runtime;
        _serializer = options.Serializer;
        _topic = MessageTypeRegistry.NormalizeName(options.Topic);
        _destination = MessageDestination.Topic(_topic);
        _typeMappings = new Dictionary<string, Type>(options.MessageTypeMappings);
        _migratedTypes = options is LegacyMessageBusOptions legacyOptions ? [.. legacyOptions.MigratedMessageTypes] : [];
        if (_migratedTypes.Contains(null!))
            throw new ArgumentException("MigratedMessageTypes has a null entry.", nameof(options));
        if (_migratedTypes.Count > 0 && runtime is null)
            throw new ArgumentException("MigratedMessageTypes needs the messaging runtime to publish them on; use AddLegacyMessageBus().", nameof(options));
        _timeProvider = options.TimeProvider;
        _loggerFactory = options.LoggerFactory;
        _logger = options.LoggerFactory.CreateLogger<LegacyMessageBusAdapter>();
    }

    /// <summary>The processors receiving for its subscribers: the legacy topic's and each shared topic's.</summary>
    internal MessageProcessor[] GetProcessors() => _processors;

    /// <summary>How many legacy type names are cached; misses are bounded so foreign names cannot grow it without limit.</summary>
    internal int CachedTypeCount => _types.Count;

    public async Task PublishAsync(Type messageType, object message, MessageOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        var now = _timeProvider.GetUtcNow();
        var headers = new Dictionary<string, string>(options?.Properties ?? new Dictionary<string, string>())
        {
            [LegacyTypeHeader] = GetTypeName(messageType),
            [KnownHeaders.SentAt] = now.ToString("O", CultureInfo.InvariantCulture)
        };
        if ((options?.CorrelationId ?? Activity.Current?.Id) is { } correlationId)
            headers[KnownHeaders.CorrelationId] = correlationId;

        byte[] body;
        try
        {
            body = _serializer.SerializeToBytes(message);
        }
        catch (Exception ex)
        {
            throw new MessageBusException($"Error publishing {messageType.Name}: {ex.Message}", ex);
        }

        // Like the original buses: delays the transport cannot hold are kept in memory and lost if the process stops.
        var delay = options?.DeliveryDelay ?? TimeSpan.Zero;
        bool holdInMemory = delay > TimeSpan.Zero && _transport.Capabilities.MaxPublishDelay is { } maxDelay && delay > maxDelay;
        var outgoing = new OutgoingMessage
        {
            MessageId = options?.UniqueId ?? Guid.NewGuid().ToString("N"),
            Body = body,
            Headers = headers,
            DeliverAt = delay > TimeSpan.Zero && !holdInMemory ? now.Add(delay) : null
        };
        bool migrated = _migratedTypes.Count > 0 && _migratedTypes.Contains(message.GetType());
        var copy = CreateSharedCopy(message, outgoing, migrated);
        var legacy = migrated ? null : outgoing;

        if (!holdInMemory)
        {
            await SendAsync(message, legacy, copy, cancellationToken).AnyContext();
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _timeProvider.SafeDelay(delay, _disposed.Token).AnyContext();
                await SendAsync(message, legacy, copy, _disposed.Token).AnyContext();
            }
            catch (OperationCanceledException) when (_disposed.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error publishing delayed {MessageType} message", messageType.Name);
            }
        }, CancellationToken.None);
    }

    public async Task SubscribeAsync<T>(Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        await EnsureReceivingAsync(cancellationToken).AnyContext();

        var subscriber = Subscriber.For(typeof(T), (message, ct) => handler((T)message, ct));
        if (subscriber.Kind != SubscriberKind.Raw && TryGetSharedRoute(subscriber.BodyType, out var route))
            await EnsureReceivingSharedTopicAsync(route.Topic, cancellationToken).AnyContext();

        // Migrated types are only published to their own topics, so subscribers of a base type or of every message read those too.
        foreach (var migrated in _migratedTypes.Where(subscriber.Accepts))
            await EnsureReceivingSharedTopicAsync(_runtime!.Types.GetRoute(migrated).Topic, cancellationToken).AnyContext();

        var id = Guid.NewGuid();
        _subscribers[id] = subscriber;
        cancellationToken.Register(() => _subscribers.TryRemove(id, out _));
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (_disposed.IsCancellationRequested)
            return;

        _disposed.Cancel();
        _subscribers.Clear();

        MessageProcessor[] processors;
        await _startLock.WaitAsync(CancellationToken.None).AnyContext();
        try
        {
            processors = [.. _sharedTopics.Values];
            if (_processor is not null)
                processors = [_processor, .. processors];

            _processor = null;
            _sharedTopics.Clear();
            _processors = [];
        }
        finally
        {
            _startLock.Release();
        }

        await Task.WhenAll(processors.Select(p => p.StopAsync(TimeSpan.Zero))).AnyContext();
    }

    /// <summary>
    /// Sends a legacy publish to the legacy topic, unless its type is migrated, and, for a type <see cref="IMessageClient"/>
    /// knows, its copy to that type's topic.
    /// </summary>
    private async Task SendAsync(object message, OutgoingMessage? legacy, (MessageDestination Destination, OutgoingMessage Message)? copy, CancellationToken cancellationToken)
    {
        if (legacy is not null)
            await _transport.SendAsync(_destination, [legacy], cancellationToken).AnyContext();
        if (copy is not { } shared)
            return;

        await _transport.SendAsync(shared.Destination, [shared.Message], cancellationToken).AnyContext();
        _runtime!.Observer?.OnSent(shared.Destination, message, shared.Message);
    }

    /// <summary>
    /// The copy of a legacy publish that goes to the topic <see cref="IMessageClient"/> publishes its type to, with the
    /// headers it adds. A <paramref name="migrated"/> type's copy is the only one sent, so it leaves out the legacy type
    /// header, which would tell legacy subscribers of that topic to skip it. Null when there is no runtime, or it does
    /// not know the type.
    /// </summary>
    private (MessageDestination Destination, OutgoingMessage Message)? CreateSharedCopy(object message, OutgoingMessage outgoing, bool migrated)
    {
        if (_runtime is null || !TryGetSharedRoute(message.GetType(), out var route))
            return null;

        var headers = new Dictionary<string, string>(outgoing.Headers.Count + 4);
        foreach (var header in outgoing.Headers)
        {
            if (!migrated || header.Key != LegacyTypeHeader)
                headers[header.Key] = header.Value;
        }

        headers[KnownHeaders.MessageType] = route.Name;
        if (_runtime.Types.GetBaseTypeNames(route.Type) is { } baseTypes)
            headers[KnownHeaders.MessageBaseTypes] = baseTypes;
        if (Activity.Current is { } current)
        {
            if (current.Id is { } traceParent)
                headers[KnownHeaders.TraceParent] = traceParent;
            if (!String.IsNullOrEmpty(current.TraceStateString))
                headers[KnownHeaders.TraceState] = current.TraceStateString;
        }

        ReadOnlyMemory<byte> body;
        try
        {
            body = ReferenceEquals(_runtime.Serializer, _serializer) ? outgoing.Body : _runtime.Serializer.SerializeToBytes<object>(message);
        }
        catch (Exception ex)
        {
            throw new MessageBusException($"Error publishing {route.Type.Name}: {ex.Message}", ex);
        }

        return (MessageDestination.Topic(route.Topic), outgoing with { Body = body, Headers = headers });
    }

    /// <summary>
    /// Whether <see cref="IMessageClient"/> knows <paramref name="type"/>: it is mapped, handled by a registered handler
    /// (or derives from a handled type in the same assembly), has a <see cref="MessageAttribute"/>, was sent before, or
    /// is migrated.
    /// </summary>
    private bool TryGetSharedRoute(Type type, out MessageRoute route)
    {
        route = null!;
        if (_runtime is null)
            return false;

        if (_runtime.Types.TryGetRoute(type, out route))
            return true;

        if (_migratedTypes.Contains(type))
        {
            route = _runtime.Types.GetRoute(type);
            return true;
        }

        if (!_attributedTypes.GetOrAdd(type, static t => t.GetCustomAttribute<MessageAttribute>(inherit: false) is not null))
            return false;

        route = _runtime.Types.GetRoute(type);
        return true;
    }

    private Task EnsureReceivingAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _processor) is not null)
            return Task.CompletedTask;

        return StartAsync(cancellationToken);

        async Task StartAsync(CancellationToken ct)
        {
            await _startLock.WaitAsync(ct).AnyContext();
            try
            {
                if (_processor is null && !_disposed.IsCancellationRequested)
                    _processor = await StartProcessorAsync(_topic, isSharedTopic: false, ct).AnyContext();
            }
            finally
            {
                _startLock.Release();
            }
        }
    }

    /// <summary>Receives what <see cref="IMessageClient"/> publishes to <paramref name="topic"/>, once per topic.</summary>
    private async Task EnsureReceivingSharedTopicAsync(string topic, CancellationToken cancellationToken)
    {
        if (topic == _topic)
            return;

        await _startLock.WaitAsync(cancellationToken).AnyContext();
        try
        {
            if (!_sharedTopics.ContainsKey(topic) && !_disposed.IsCancellationRequested)
                _sharedTopics[topic] = await StartProcessorAsync(topic, isSharedTopic: true, cancellationToken).AnyContext();
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task<MessageProcessor> StartProcessorAsync(string topic, bool isSharedTopic, CancellationToken cancellationToken)
    {
        // Like the original buses, messages are handled one at a time in arrival order, and a subscriber is never
        // called concurrently with itself; the subscribers of one message run concurrently.
        var settings = new MessageProcessorSettings
        {
            MaxConcurrency = Math.Max(1, Math.Min(_transport.Capabilities.MaxReceiveBatchSize, MaxBatchSize)),
            InOrder = true,
            LeaseDuration = LeaseDuration,
            ReplaceTimeout = ReplaceTimeout,
            Description = isSharedTopic ? $"legacy message bus subscribers of {topic}" : "legacy message bus subscribers"
        };
        var processor = new MessageProcessor(_transport, MessageSource.InstanceSubscription(topic, "legacy-bus"), settings, new DeliveryHandler(this, isSharedTopic),
            new ProcessingStatusTracker(_timeProvider), _timeProvider, _loggerFactory);

        // Listed before it starts receiving, so the test harness knows its receives from the first.
        _processors = [.. _processors, processor];
        try
        {
            await processor.StartAsync(cancellationToken).AnyContext();
        }
        catch
        {
            _processors = [.. _processors.Where(p => p != processor)];
            throw;
        }

        return processor;
    }

    private async Task<bool> HandleAsync(MessageDelivery delivery, bool isSharedTopic, CancellationToken abortToken)
    {
        await DispatchAsync(delivery.Message, isSharedTopic, abortToken).AnyContext();
        if (delivery.IsLeaseLost)
            return false;

        return await delivery.SettleAsync(static (receiver, message, ct) => receiver.CompleteAsync(message, ct), "complete", _logger).AnyContext() is null;
    }

    private async Task DispatchAsync(IncomingMessage message, bool isSharedTopic, CancellationToken cancellationToken)
    {
        Type? type;
        ISerializer serializer;
        bool fromMessageClient = false;
        if (message.Headers.TryGetValue(LegacyTypeHeader, out string? typeName))
        {
            // A legacy publish's copy: its subscribers here already received it from the legacy topic.
            if (isSharedTopic)
                return;

            type = GetType(typeName);
            serializer = _serializer;
        }
        else if (_runtime is not null && _runtime.Types.TryGetType(message.Headers, out var known))
        {
            type = known;
            typeName = GetTypeName(known);
            serializer = _runtime.Serializer;
            fromMessageClient = true;
        }
        else
        {
            if (isSharedTopic)
                return;

            type = null;
            serializer = _serializer;
        }

        var subscribers = _subscribers.Values.Where(s => s.Accepts(type)).ToList();
        if (subscribers.Count == 0)
            return;

        var body = new Lazy<object?>(() => type is null ? null : serializer.Deserialize(message.Body, type), LazyThreadSafetyMode.ExecutionAndPublication);
        var raw = new Message(message.Body, _ => body.Value)
        {
            UniqueId = message.MessageId,
            CorrelationId = message.Headers.GetValueOrDefault(KnownHeaders.CorrelationId),
            Type = typeName,
            ClrType = type,
            Properties = message.Headers.Where(h => fromMessageClient ? !IsMessageClientHeader(h.Key) : h.Key is not LegacyTypeHeader and not KnownHeaders.SentAt and not KnownHeaders.CorrelationId)
                .ToDictionary(h => h.Key, h => h.Value)
        };

        string? parentId = fromMessageClient ? message.Headers.GetValueOrDefault(KnownHeaders.TraceParent) ?? raw.CorrelationId : raw.CorrelationId;
        using var activity = FoundatioDiagnostics.ActivitySource.StartActivity("HandleMessage", ActivityKind.Internal, parentId);
        if (activity is not null)
            activity.DisplayName = $"Message: {type?.Name ?? typeName}";

        if (subscribers.Count == 1)
            await InvokeAsync(subscribers[0], message, raw, body, typeName, cancellationToken).AnyContext();
        else
            await Task.WhenAll(subscribers.Select(s => Task.Run(() => InvokeAsync(s, message, raw, body, typeName, cancellationToken), CancellationToken.None))).AnyContext();
    }

    private async Task InvokeAsync(Subscriber subscriber, IncomingMessage message, Message raw, Lazy<object?> body, string? typeName, CancellationToken cancellationToken)
    {
        try
        {
            object? argument = subscriber.Kind switch
            {
                SubscriberKind.Raw => raw,
                SubscriberKind.Typed => Activator.CreateInstance(typeof(Message<>).MakeGenericType(subscriber.BodyType), raw),
                _ => body.Value
            };
            if (argument is null || subscriber.Kind == SubscriberKind.Typed && body.Value is null)
                return;

            await subscriber.Handler(argument, cancellationToken).AnyContext();
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
                _logger.LogError(ex, "Error handling legacy bus message {MessageId} of type {MessageType}", message.MessageId, typeName);
        }
    }

    private static bool IsMessageClientHeader(string name) => name is KnownHeaders.MessageType or KnownHeaders.MessageBaseTypes or KnownHeaders.SentAt
        or KnownHeaders.CorrelationId or KnownHeaders.TraceParent or KnownHeaders.TraceState or KnownHeaders.DeliverAt or KnownHeaders.ExpiresAt;

    private string GetTypeName(Type type)
    {
        return _typeNames.GetOrAdd(type, t => _typeMappings.FirstOrDefault(m => m.Value == t).Key ?? String.Concat(t.FullName, ", ", t.Assembly.GetName().Name));
    }

    private void ThrowIfDisposed()
    {
        if (_disposed.IsCancellationRequested)
            throw new MessageBusException("The message bus has been disposed.");
    }

    /// <summary>Resolves a legacy type name. Hits are always cached; misses only up to a limit, since any sender can make them up.</summary>
    private Type? GetType(string typeName)
    {
        if (_types.TryGetValue(typeName, out var cached))
            return cached;

        Type? type;
        if (_typeMappings.TryGetValue(typeName, out var mapped))
        {
            type = mapped;
        }
        else
        {
            try
            {
                type = Type.GetType(typeName, throwOnError: false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not resolve legacy bus message type {MessageType}", typeName);
                type = null;
            }
        }

        if (type is not null)
            _types.TryAdd(typeName, type);
        else if (Volatile.Read(ref _typeMisses) < MaxCachedTypeMisses && Interlocked.Increment(ref _typeMisses) <= MaxCachedTypeMisses && !_types.TryAdd(typeName, null))
            Interlocked.Decrement(ref _typeMisses);

        return type;
    }

    /// <summary>Handles the deliveries of one topic: the legacy topic, or a topic shared with <see cref="IMessageClient"/>.</summary>
    private sealed class DeliveryHandler(LegacyMessageBusAdapter bus, bool isSharedTopic) : IMessageDeliveryHandler
    {
        public Task<bool> HandleAsync(MessageDelivery delivery, CancellationToken abortToken) => bus.HandleAsync(delivery, isSharedTopic, abortToken);

        public void OnReleased(MessageDelivery delivery, string outcome)
        {
        }
    }

    private enum SubscriberKind
    {
        Body,
        Raw,
        Typed
    }

    /// <summary>A subscriber to message bodies, raw <see cref="IMessage"/>s, or <see cref="IMessage{T}"/>s.</summary>
    private sealed record Subscriber(SubscriberKind Kind, Type BodyType, Func<object, CancellationToken, Task> Handler)
    {
        public static Subscriber For(Type type, Func<object, CancellationToken, Task> handler)
        {
            if (type == typeof(IMessage))
                return new Subscriber(SubscriberKind.Raw, typeof(object), handler);
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IMessage<>))
                return new Subscriber(SubscriberKind.Typed, type.GetGenericArguments()[0], handler);

            return new Subscriber(SubscriberKind.Body, type, handler);
        }

        public bool Accepts(Type? messageType) => Kind == SubscriberKind.Raw || messageType is not null && BodyType.IsAssignableFrom(messageType);
    }
}

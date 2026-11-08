using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
/// derived types, delivery is best effort, and handler exceptions are logged rather than retried.
/// </remarks>
internal sealed class LegacyMessageBusAdapter : IMessageBus
{
    private const string LegacyTypeHeader = "legacy-type";

    private readonly IMessageTransport _transport;
    private readonly ISerializer _serializer;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly string _topic;
    private readonly IReadOnlyDictionary<string, Type> _typeMappings;
    private readonly ConcurrentDictionary<Type, string> _typeNames = new();
    private readonly ConcurrentDictionary<Guid, Subscriber> _subscribers = new();
    private readonly ConcurrentDictionary<string, Type?> _types = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly CancellationTokenSource _disposed = new();
    private IMessageReceiver? _receiver;
    private Task _receiveLoop = Task.CompletedTask;

    public LegacyMessageBusAdapter(IMessageTransport transport, SharedMessageBusOptions options)
    {
        _transport = transport;
        _serializer = options.Serializer;
        _topic = MessageTypeRegistry.NormalizeName(options.Topic);
        _typeMappings = new Dictionary<string, Type>(options.MessageTypeMappings);
        _timeProvider = options.TimeProvider;
        _logger = options.LoggerFactory.CreateLogger<LegacyMessageBusAdapter>();
    }

    public async Task PublishAsync(Type messageType, object message, MessageOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        var headers = new Dictionary<string, string>(options?.Properties ?? new Dictionary<string, string>())
        {
            [LegacyTypeHeader] = GetTypeName(messageType),
            [KnownHeaders.SentAt] = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)
        };
        if ((options?.CorrelationId ?? System.Diagnostics.Activity.Current?.Id) is { } correlationId)
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

        var outgoing = new OutgoingMessage
        {
            MessageId = options?.UniqueId ?? Guid.NewGuid().ToString("N"),
            Body = body,
            Headers = headers
        };

        var delay = options?.DeliveryDelay ?? TimeSpan.Zero;
        if (delay <= TimeSpan.Zero)
        {
            await _transport.SendAsync(MessageDestination.Topic(_topic), [outgoing], cancellationToken).AnyContext();
            return;
        }

        if (_transport.Capabilities.MaxDelay is not { } maxDelay || delay <= maxDelay)
        {
            await _transport.SendAsync(MessageDestination.Topic(_topic), [outgoing with { DeliverAt = _timeProvider.GetUtcNow().Add(delay) }], cancellationToken).AnyContext();
            return;
        }

        // Like the original buses: delays the transport cannot hold are kept in memory and lost if the process stops.
        _ = Task.Run(async () =>
        {
            try
            {
                await _timeProvider.SafeDelay(delay, _disposed.Token).AnyContext();
                await _transport.SendAsync(MessageDestination.Topic(_topic), [outgoing], _disposed.Token).AnyContext();
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

        var id = Guid.NewGuid();
        _subscribers[id] = Subscriber.For(typeof(T), (message, ct) => handler((T)message, ct));
        cancellationToken.Register(() => _subscribers.TryRemove(id, out _));
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (_disposed.IsCancellationRequested)
            return;

        _disposed.Cancel();
        _subscribers.Clear();
        await _receiveLoop.AnyContext();
        if (_receiver is not null)
            await _receiver.DisposeAsync().AnyContext();
    }

    private async Task EnsureReceivingAsync(CancellationToken cancellationToken)
    {
        if (_receiver is not null)
            return;

        await _startLock.WaitAsync(cancellationToken).AnyContext();
        try
        {
            if (_receiver is not null)
                return;

            _receiver = await _transport.CreateReceiverAsync(MessageSource.InstanceSubscription(_topic, "legacy-bus"), new ReceiverOptions(), cancellationToken).AnyContext();
            _receiveLoop = Task.Run(ReceiveLoopAsync, CancellationToken.None);
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var token = _disposed.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                foreach (var message in await _receiver!.ReceiveAsync(100, TimeSpan.FromSeconds(5), token).AnyContext())
                {
                    await DispatchAsync(message, token).AnyContext();
                    await _receiver.CompleteAsync(message, CancellationToken.None).AnyContext();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error receiving legacy bus messages");
                await _timeProvider.SafeDelay(TimeSpan.FromSeconds(1), token).AnyContext();
            }
        }
    }

    private async Task DispatchAsync(IncomingMessage message, CancellationToken cancellationToken)
    {
        message.Headers.TryGetValue(LegacyTypeHeader, out string? typeName);
        var type = typeName is null ? null : GetType(typeName);
        var subscribers = _subscribers.Values.Where(s => s.Accepts(type)).ToList();
        if (subscribers.Count == 0)
            return;

        object? body = null;
        bool deserialized = false;
        object? GetBody()
        {
            if (!deserialized && type is not null)
            {
                body = _serializer.Deserialize(message.Body, type);
                deserialized = true;
            }

            return body;
        }

        var raw = new Message(message.Body, _ => GetBody())
        {
            UniqueId = message.MessageId,
            CorrelationId = message.Headers.GetValueOrDefault(KnownHeaders.CorrelationId),
            Type = typeName,
            ClrType = type,
            Properties = message.Headers.Where(h => h.Key is not LegacyTypeHeader and not KnownHeaders.SentAt and not KnownHeaders.CorrelationId).ToDictionary(h => h.Key, h => h.Value)
        };

        using var activity = FoundatioDiagnostics.ActivitySource.StartActivity("HandleMessage", System.Diagnostics.ActivityKind.Internal, raw.CorrelationId);
        if (activity is not null)
            activity.DisplayName = $"Message: {type?.Name ?? typeName}";

        foreach (var subscriber in subscribers)
        {
            try
            {
                object? argument = subscriber.Kind switch
                {
                    SubscriberKind.Raw => raw,
                    SubscriberKind.Typed => Activator.CreateInstance(typeof(Message<>).MakeGenericType(subscriber.BodyType), raw),
                    _ => GetBody()
                };
                if (argument is null || subscriber.Kind == SubscriberKind.Typed && GetBody() is null)
                    continue;

                await subscriber.Handler(argument, cancellationToken).AnyContext();
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Error handling legacy bus message {MessageId} of type {MessageType}", message.MessageId, typeName);
            }
        }
    }

    private string GetTypeName(Type type)
    {
        return _typeNames.GetOrAdd(type, t => _typeMappings.FirstOrDefault(m => m.Value == t).Key ?? String.Concat(t.FullName, ", ", t.Assembly.GetName().Name));
    }

    private void ThrowIfDisposed()
    {
        if (_disposed.IsCancellationRequested)
            throw new MessageBusException("The message bus has been disposed.");
    }

    private Type? GetType(string typeName)
    {
        return _types.GetOrAdd(typeName, name =>
        {
            if (_typeMappings.TryGetValue(name, out var mapped))
                return mapped;

            try
            {
                return Type.GetType(name, throwOnError: false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not resolve legacy bus message type {MessageType}", name);
                return null;
            }
        });
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

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Resilience;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Messaging;

public abstract class MessageBusBase<TOptions> : IMessageBus, IHaveLogger, IHaveLoggerFactory, IHaveTimeProvider, IHaveResiliencePolicyProvider, IDisposable where TOptions : SharedMessageBusOptions
{
    protected readonly ConcurrentDictionary<string, Subscriber> _subscribers = new();
    protected readonly TOptions _options;
    protected readonly ILogger _logger;
    protected readonly ILoggerFactory _loggerFactory;
    protected readonly TimeProvider _timeProvider;
    protected readonly IResiliencePolicyProvider _resiliencePolicyProvider;
    protected readonly IResiliencePolicy _resiliencePolicy;
    protected readonly ISerializer _serializer;
    private readonly CancellationTokenSource _disposedCancellationTokenSource = new();
    private bool _isDisposed;

    public MessageBusBase(TOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _loggerFactory = options?.LoggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger(GetType());
        _timeProvider = options.TimeProvider ?? TimeProvider.System;

        _resiliencePolicyProvider = options.ResiliencePolicyProvider;
        _resiliencePolicy = _resiliencePolicyProvider.GetPolicy<MessageBusBase<TOptions>, IMessageBus>(
            builder => builder.WithUnhandledException<MessageBusException>(),
            _logger, _timeProvider);

        _serializer = options.Serializer ?? DefaultSerializer.Instance;
        MessageBusId = _options.Topic + Guid.NewGuid().ToString("N").Substring(10);
    }

    /// <summary>
    /// Gets a cancellation token that is canceled when this instance is disposed.
    /// Use this token to cancel background operations during shutdown.
    /// </summary>
    protected CancellationToken DisposedCancellationToken => _disposedCancellationTokenSource.Token;

    ILogger IHaveLogger.Logger => _logger;
    ILoggerFactory IHaveLoggerFactory.LoggerFactory => _loggerFactory;
    TimeProvider IHaveTimeProvider.TimeProvider => _timeProvider;
    IResiliencePolicyProvider IHaveResiliencePolicyProvider.ResiliencePolicyProvider => _resiliencePolicyProvider;

    protected virtual Task EnsureTopicCreatedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected abstract Task PublishImplAsync(string messageType, object message, MessageOptions options, CancellationToken cancellationToken);

    public async Task PublishAsync(Type messageType, object message, MessageOptions options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        options ??= new MessageOptions();

        if (String.IsNullOrEmpty(options.CorrelationId))
        {
            options.CorrelationId = Activity.Current?.Id;
            if (!String.IsNullOrEmpty(Activity.Current?.TraceStateString))
                options.Properties.Add("TraceState", Activity.Current.TraceStateString);
        }

        try
        {
            await EnsureTopicCreatedAsync(cancellationToken).AnyContext();
            await PublishImplAsync(GetMappedMessageType(messageType), message, options, cancellationToken).AnyContext();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not MessageBusException)
        {
            throw new MessageBusException($"Error publishing {messageType.Name}: {ex.Message}", ex);
        }
    }

    private readonly ConcurrentDictionary<Type, string> _mappedMessageTypesCache = new();
    protected string GetMappedMessageType(Type messageType)
    {
        return _mappedMessageTypesCache.GetOrAdd(messageType, type =>
        {
            var reversedMap = _options.MessageTypeMappings.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);
            if (reversedMap.ContainsKey(type))
                return reversedMap[type];

            return String.Concat(messageType.FullName, ", ", messageType.Assembly.GetName().Name);
        });
    }

    private readonly ConcurrentDictionary<string, Type> _knownMessageTypesCache = new();
    protected virtual Type GetMappedMessageType(string messageType)
    {
        if (String.IsNullOrEmpty(messageType))
            return null;

        return _knownMessageTypesCache.GetOrAdd(messageType, type =>
        {
            if (_options.MessageTypeMappings != null && _options.MessageTypeMappings.ContainsKey(type))
                return _options.MessageTypeMappings[type];

            try
            {
                return Type.GetType(type);
            }
            catch (Exception)
            {
                try
                {
                    string[] typeParts = type.Split(',');
                    if (typeParts.Length >= 2)
                        type = String.Join(",", typeParts[0], typeParts[1]);

                    // try resolve type without version
                    return Type.GetType(type);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error getting message body type: {MessageType}", type);

                    return null;
                }
            }
        });
    }

    protected virtual Task RemoveTopicSubscriptionAsync() => Task.CompletedTask;
    protected virtual Task EnsureTopicSubscriptionAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected virtual Task SubscribeImplAsync<T>(Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken) where T : class
    {
        var subscriber = new Subscriber
        {
            CancellationToken = cancellationToken,
            Type = typeof(T),
            Action = (message, token) =>
            {
                if (message is T typedMessage)
                    return handler(typedMessage, cancellationToken);

                _logger.LogTrace("Unable to call subscriber action: {MessageType} cannot be safely casted to {SubscriberType}", message.GetType(), typeof(T));
                return Task.CompletedTask;
            }
        };

        if (cancellationToken != CancellationToken.None)
        {
            cancellationToken.Register(() =>
            {
                _subscribers.TryRemove(subscriber.Id, out _);
                if (_subscribers.Count == 0)
                {
                    _logger.LogDebug("Removing topic subscription for {MessageBusId}: No subscribers", MessageBusId);
                    RemoveTopicSubscriptionAsync().GetAwaiter().GetResult();
                }
            });
        }

        if (subscriber.Type.Name == "IMessage`1" && subscriber.Type.GenericTypeArguments.Length == 1)
        {
            var modelType = subscriber.Type.GenericTypeArguments.Single();
            subscriber.GenericType = typeof(Message<>).MakeGenericType(modelType);
        }

        if (!_subscribers.TryAdd(subscriber.Id, subscriber))
            _logger.LogError("Unable to add subscriber {SubscriberId}", subscriber.Id);

        return Task.CompletedTask;
    }

    public async Task SubscribeAsync<T>(Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken = default) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogTrace("Adding subscriber for {MessageType}", typeof(T).FullName);

        await SubscribeImplAsync(handler, cancellationToken).AnyContext();
        await EnsureTopicSubscriptionAsync(cancellationToken).AnyContext();
    }

    protected List<Subscriber> GetMessageSubscribers(IMessage message)
    {
        return _subscribers.Values.Where(s => SubscriberHandlesMessage(s, message)).ToList();
    }

    protected virtual bool SubscriberHandlesMessage(Subscriber subscriber, IMessage message)
    {
        if (subscriber.Type == typeof(IMessage))
            return true;

        var clrType = message.ClrType ?? GetMappedMessageType(message.Type);
        if (clrType is null)
        {
            _logger.LogWarning("Unable to resolve CLR type for message body type: ClrType={MessageClrType} Type={MessageType}", message.ClrType, message.Type);
            return false;
        }

        if (subscriber.IsAssignableFrom(clrType))
            return true;

        return false;
    }

    protected virtual byte[] SerializeMessageBody(string messageType, object body)
    {
        if (body == null)
            return [];

        return _serializer.SerializeToBytes(body);
    }

    protected virtual object DeserializeMessageBody(IMessage message)
    {
        if (message.Data is null || message.Data.Length == 0)
            return null;

        object body;
        try
        {
            var clrType = message.ClrType ?? GetMappedMessageType(message.Type);
            body = clrType != null ? _serializer.Deserialize(message.Data, clrType) : message.Data;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deserializing message body: {Message}", ex.Message);

            return null;
        }

        return body;
    }

    protected async Task SendMessageToSubscribersAsync(IMessage message)
    {
        var subscribers = GetMessageSubscribers(message);

        _logger.LogTrace("Found {SubscriberCount} subscribers for message type: ClrType={MessageClrType} Type={MessageType}", subscribers.Count, message.ClrType, message.Type);

        if (subscribers.Count == 0)
            return;

        var subscriberHandlers = subscribers.Select(subscriber =>
        {
            if (subscriber.CancellationToken.IsCancellationRequested)
            {
                if (_subscribers.TryRemove(subscriber.Id, out _))
                {
                    _logger.LogTrace("Removed cancelled subscriber: {SubscriberId}", subscriber.Id);
                }
                else
                {
                    _logger.LogTrace("Unable to remove cancelled subscriber: {SubscriberId}", subscriber.Id);
                }

                return Task.CompletedTask;
            }

            return Task.Run(async () =>
            {
                if (subscriber.CancellationToken.IsCancellationRequested)
                {
                    _logger.LogTrace("The cancelled subscriber action will not be called: {SubscriberId}", subscriber.Id);
                    return;
                }

                _logger.LogTrace("Calling subscriber action: {SubscriberId}", subscriber.Id);
                using var activity = StartHandleMessageActivity(message);

                using (_logger.BeginScope(s => s
                           .PropertyIf("UniqueId", message.UniqueId, !String.IsNullOrEmpty(message.UniqueId))
                           .PropertyIf("CorrelationId", message.CorrelationId, !String.IsNullOrEmpty(message.CorrelationId))))
                {
                    if (subscriber.Type == typeof(IMessage))
                    {
                        await subscriber.Action(message, subscriber.CancellationToken).AnyContext();
                    }
                    else if (subscriber.GenericType != null)
                    {
                        object typedMessage = Activator.CreateInstance(subscriber.GenericType, message);
                        await subscriber.Action(typedMessage, subscriber.CancellationToken).AnyContext();
                    }
                    else
                    {
                        await subscriber.Action(message.GetBody(), subscriber.CancellationToken).AnyContext();
                    }
                }

                _logger.LogTrace("Finished calling subscriber action: {SubscriberId}", subscriber.Id);
            }, DisposedCancellationToken);
        });

        try
        {
            await Task.WhenAll(subscriberHandlers.ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending message to subscribers: {Message}", ex.Message);
            throw new MessageBusException($"Error sending message to subscribers: {ex.Message}", ex);
        }

        _logger.LogTrace("Done enqueueing message to {SubscriberCount} subscribers for message type {MessageType}", subscribers.Count, message.Type);
    }

    protected virtual Activity StartHandleMessageActivity(IMessage message)
    {
        var activity = FoundatioDiagnostics.ActivitySource.StartActivity("HandleMessage", ActivityKind.Internal, message.CorrelationId);
        if (activity is null)
            return null;

        if (message.Properties != null && message.Properties.TryGetValue("TraceState", out string traceState))
            activity.TraceStateString = traceState;

        activity.DisplayName = $"Message: {message.ClrType?.Name ?? message.Type}";

        EnrichHandleMessageActivity(activity, message);
        return activity;
    }

    protected virtual void EnrichHandleMessageActivity(Activity activity, IMessage message)
    {
        if (!activity.IsAllDataRequested)
            return;

        activity.AddTag("MessageType", message.Type);
        activity.AddTag("ClrType", message.ClrType?.FullName);
        activity.AddTag("UniqueId", message.UniqueId);
        activity.AddTag("CorrelationId", message.CorrelationId);

        if (message.Properties is not { Count: > 0 })
            return;

        foreach (var p in message.Properties)
        {
            if (p.Key != "TraceState")
                activity.AddTag(p.Key, p.Value);
        }
    }

    /// <summary>
    /// Schedules a message for delayed delivery using an in-memory timer.
    /// </summary>
    /// <remarks>
    /// This method calls <see cref="PublishAsync"/> (not <see cref="PublishImplAsync"/>) to ensure
    /// topic infrastructure is re-established if needed (e.g., after reconnection for external providers).
    /// The <see cref="MessageOptions.DeliveryDelay"/> is cleared via record copy to prevent infinite recursion.
    /// The Properties dictionary is cloned to avoid shared mutable state between the caller and delayed delivery.
    /// </remarks>
    protected void SendDelayedMessage(Type messageType, object message, MessageOptions options)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(options);

        var delay = options.DeliveryDelay.GetValueOrDefault();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(delay, TimeSpan.Zero);

        // Clone options to capture current state and avoid shared mutable state
        var clonedOptions = options with
        {
            DeliveryDelay = null, // Clear to prevent infinite recursion
            Properties = new Dictionary<string, string>(options.Properties)
        };

        var sendTime = _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(delay);
        Task.Factory.StartNew(async () =>
        {
            await _timeProvider.SafeDelay(delay, _disposedCancellationTokenSource.Token).AnyContext();
            if (_disposedCancellationTokenSource.IsCancellationRequested)
            {
                _logger.LogTrace("Discarding delayed message scheduled for {SendTime:O} for type {MessageType}", sendTime, messageType);
                return;
            }

            _logger.LogTrace("Sending delayed message scheduled for {SendTime:O} for type {MessageType}", sendTime, messageType);

            try
            {
                await _resiliencePolicy.ExecuteAsync(async ct =>
                {
                    await PublishAsync(messageType, message, clonedOptions, ct).AnyContext();
                }, _disposedCancellationTokenSource.Token).AnyContext();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish delayed message for type {MessageType}: {Message}", messageType, ex.Message);
            }
        }, _disposedCancellationTokenSource.Token);
    }

    public string MessageBusId { get; init; }

    public virtual void Dispose()
    {
        if (_isDisposed)
        {
            _logger.LogTrace("MessageBus {MessageBusId} dispose was already called", MessageBusId);
            return;
        }

        _isDisposed = true;

        _logger.LogTrace("MessageBus {MessageBusId} dispose", MessageBusId);
        _subscribers?.Clear();
        _disposedCancellationTokenSource.Cancel();
        _disposedCancellationTokenSource.Dispose();
    }

    [DebuggerDisplay("MessageType: {MessageType} SendTime: {SendTime} Message: {Message}")]
    protected class DelayedMessage
    {
        public DateTime SendTime { get; set; }
        public Type MessageType { get; set; }
        public object Message { get; set; }
    }

    [DebuggerDisplay("Id: {Id} Type: {Type} CancellationToken: {CancellationToken}")]
    protected class Subscriber
    {
        private readonly ConcurrentDictionary<Type, bool> _assignableTypesCache = new();

        public string Id { get; private set; } = Guid.NewGuid().ToString("N");
        public CancellationToken CancellationToken { get; set; }
        public Type Type { get; set; }
        public Type GenericType { get; set; }
        public Func<object, CancellationToken, Task> Action { get; set; }

        public bool IsAssignableFrom(Type type)
        {
            if (type is null)
                return false;

            return _assignableTypesCache.GetOrAdd(type, t =>
            {
                if (t.IsClass)
                {
                    var typedMessageType = typeof(IMessage<>).MakeGenericType(t);
                    if (Type == typedMessageType)
                        return true;
                }

                return Type.GetTypeInfo().IsAssignableFrom(t);
            });
        }
    }
}

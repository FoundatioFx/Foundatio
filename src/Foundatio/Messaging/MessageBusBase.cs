using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Messaging;

public abstract class MessageBusBase<TOptions> : IMessageBus, IDisposable where TOptions : SharedMessageBusOptions
{
    private readonly CancellationTokenSource _messageBusDisposedCancellationTokenSource;
    protected readonly ConcurrentDictionary<string, Subscriber> _subscribers = new();
    protected readonly TOptions _options;
    protected readonly ILogger _logger;
    protected readonly TimeProvider _timeProvider;
    protected readonly ISerializer _serializer;
    private bool _isDisposed;

    public MessageBusBase(TOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        var loggerFactory = options?.LoggerFactory ?? NullLoggerFactory.Instance;
        _logger = loggerFactory.CreateLogger(GetType());
        _timeProvider = options.TimeProvider;
        _serializer = options.Serializer ?? DefaultSerializer.Instance;
        MessageBusId = _options.Topic + Guid.NewGuid().ToString("N").Substring(10);
        _messageBusDisposedCancellationTokenSource = new CancellationTokenSource();
    }

    protected virtual Task EnsureTopicCreatedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    protected abstract Task PublishImplAsync(string messageType, object message, MessageOptions options, CancellationToken cancellationToken);
    public async Task PublishAsync(Type messageType, object message, MessageOptions options = null, CancellationToken cancellationToken = default)
    {
        if (messageType == null || message == null)
            return;

        options ??= new MessageOptions();

        if (String.IsNullOrEmpty(options.CorrelationId))
        {
            options.CorrelationId = Activity.Current?.Id;
            if (!String.IsNullOrEmpty(Activity.Current?.TraceStateString))
                options.Properties.Add("TraceState", Activity.Current.TraceStateString);
        }

        await EnsureTopicCreatedAsync(cancellationToken).AnyContext();
        await PublishImplAsync(GetMappedMessageType(messageType), message, options ?? new MessageOptions(), cancellationToken).AnyContext();
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
                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning(ex, "Error getting message body type: {MessageType}", type);

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
                if (message is not T)
                {
                    if (_logger.IsEnabled(LogLevel.Trace))
                        _logger.LogTrace("Unable to call subscriber action: {MessageType} cannot be safely casted to {SubscriberType}", message.GetType(), typeof(T));
                    return Task.CompletedTask;
                }

                return handler((T)message, cancellationToken);
            }
        };

        if (cancellationToken != CancellationToken.None)
        {
            cancellationToken.Register(() =>
            {
                _subscribers.TryRemove(subscriber.Id, out _);
                if (_subscribers.Count == 0)
                    RemoveTopicSubscriptionAsync().GetAwaiter().GetResult();
            });
        }

        if (subscriber.Type.Name == "IMessage`1" && subscriber.Type.GenericTypeArguments.Length == 1)
        {
            var modelType = subscriber.Type.GenericTypeArguments.Single();
            subscriber.GenericType = typeof(Message<>).MakeGenericType(modelType);
        }

        if (!_subscribers.TryAdd(subscriber.Id, subscriber) && _logger.IsEnabled(LogLevel.Error))
            _logger.LogError("Unable to add subscriber {SubscriberId}", subscriber.Id);

        return Task.CompletedTask;
    }

    public async Task SubscribeAsync<T>(Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken = default) where T : class
    {
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Adding subscriber for {MessageType}.", typeof(T).FullName);

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
            if (_logger.IsEnabled(LogLevel.Warning))
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
            return Array.Empty<byte>();

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
            if (_logger.IsEnabled(LogLevel.Error))
                _logger.LogError(ex, "Error deserializing message body: {Message}", ex.Message);

            return null;
        }

        return body;
    }

    protected async Task SendMessageToSubscribersAsync(IMessage message)
    {
        bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
        var subscribers = GetMessageSubscribers(message);

        if (isTraceLogLevelEnabled)
            _logger.LogTrace("Found {SubscriberCount} subscribers for message type: ClrType={MessageClrType} Type={MessageType}", subscribers.Count, message.ClrType, message.Type);

        if (subscribers.Count == 0)
            return;

        var subscriberHandlers = subscribers.Select(subscriber =>
        {
            if (subscriber.CancellationToken.IsCancellationRequested)
            {
                if (_subscribers.TryRemove(subscriber.Id, out _))
                {
                    if (isTraceLogLevelEnabled)
                        _logger.LogTrace("Removed cancelled subscriber: {SubscriberId}", subscriber.Id);
                }
                else if (isTraceLogLevelEnabled)
                {
                    _logger.LogTrace("Unable to remove cancelled subscriber: {SubscriberId}", subscriber.Id);
                }

                return Task.CompletedTask;
            }

            return Task.Run(async () =>
            {
                if (subscriber.CancellationToken.IsCancellationRequested)
                {
                    if (isTraceLogLevelEnabled)
                        _logger.LogTrace("The cancelled subscriber action will not be called: {SubscriberId}", subscriber.Id);

                    return;
                }

                if (isTraceLogLevelEnabled)
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

                if (isTraceLogLevelEnabled)
                    _logger.LogTrace("Finished calling subscriber action: {SubscriberId}", subscriber.Id);
            });
        });

        try
        {
            await Task.WhenAll(subscriberHandlers.ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error sending message to subscribers: {Message}", ex.Message);

            throw;
        }

        if (isTraceLogLevelEnabled)
            _logger.LogTrace("Done enqueueing message to {SubscriberCount} subscribers for message type {MessageType}", subscribers.Count, message.Type);
    }

    protected virtual Activity StartHandleMessageActivity(IMessage message)
    {
        var activity = FoundatioDiagnostics.ActivitySource.StartActivity("HandleMessage", ActivityKind.Server, message.CorrelationId);
        if (activity is null)
            return null;

        if (message.Properties != null && message.Properties.TryGetValue("TraceState", out var traceState))
            activity.TraceStateString = traceState.ToString();

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

        if (message.Properties == null || message.Properties.Count <= 0)
            return;

        foreach (var p in message.Properties)
        {
            if (p.Key != "TraceState")
                activity.AddTag(p.Key, p.Value);
        }
    }

    protected Task AddDelayedMessageAsync(Type messageType, object message, TimeSpan delay)
    {
        if (message == null)
            throw new ArgumentNullException(nameof(message));

        SendDelayedMessage(messageType, message, delay);

        return Task.CompletedTask;
    }

    protected void SendDelayedMessage(Type messageType, object message, TimeSpan delay)
    {
        if (message == null)
            throw new ArgumentNullException(nameof(message));

        if (delay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delay));

        var sendTime = _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(delay);
        Task.Factory.StartNew(async () =>
        {
            await _timeProvider.SafeDelay(delay, _messageBusDisposedCancellationTokenSource.Token).AnyContext();

            bool isTraceLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
            if (_messageBusDisposedCancellationTokenSource.IsCancellationRequested)
            {
                if (isTraceLevelEnabled)
                    _logger.LogTrace("Discarding delayed message scheduled for {SendTime:O} for type {MessageType}", sendTime, messageType);
                return;
            }

            if (isTraceLevelEnabled)
                _logger.LogTrace("Sending delayed message scheduled for {SendTime:O} for type {MessageType}", sendTime, messageType);

            await PublishAsync(messageType, message).AnyContext();
        });
    }

    public string MessageBusId { get; protected set; }

    public virtual void Dispose()
    {
        if (_isDisposed)
        {
            _logger.LogTrace("MessageBus {0} dispose was already called.", MessageBusId);
            return;
        }

        _isDisposed = true;

        _logger.LogTrace("MessageBus {0} dispose", MessageBusId);
        _subscribers?.Clear();
        _messageBusDisposedCancellationTokenSource?.Cancel();
        _messageBusDisposedCancellationTokenSource?.Dispose();
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

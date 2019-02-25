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

namespace Foundatio.Messaging {
    public abstract class MessageBusBase<TOptions> : IMessageBus, IDisposable where TOptions : SharedMessageBusOptions {
        private readonly CancellationTokenSource _messageBusDisposedCancellationTokenSource;
        protected readonly ConcurrentDictionary<string, Subscriber> _subscribers = new ConcurrentDictionary<string, Subscriber>();
        protected readonly TOptions _options;
        protected readonly ILogger _logger;
        private bool _isDisposed;

        public MessageBusBase(TOptions options) {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            var loggerFactory = options?.LoggerFactory ?? NullLoggerFactory.Instance;
            _logger = loggerFactory.CreateLogger(GetType());
            MessageBusId = _options.Topic + Guid.NewGuid().ToString("N").Substring(10);
            _messageBusDisposedCancellationTokenSource = new CancellationTokenSource();
        }

        protected virtual Task EnsureTopicCreatedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        protected abstract Task PublishImplAsync(string messageType, object message, TimeSpan? delay, CancellationToken cancellationToken);
        public async Task PublishAsync(Type messageType, object message, TimeSpan? delay = null, CancellationToken cancellationToken = default) {
            if (messageType == null || message == null)
                return;

            await EnsureTopicCreatedAsync(cancellationToken).AnyContext();
            await PublishImplAsync(GetMappedMessageType(messageType), message, delay, cancellationToken).AnyContext();
        }

        private readonly ConcurrentDictionary<Type, string> _mappedMessageTypesCache = new ConcurrentDictionary<Type, string>();
        protected string GetMappedMessageType(Type messageType) {
            return _mappedMessageTypesCache.GetOrAdd(messageType, type => {
                var reversedMap = _options.MessageTypeMappings.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);
                if (reversedMap.ContainsKey(type))
                    return reversedMap[type];
                
                return String.Concat(messageType.FullName, ", ", messageType.Assembly.GetName().Name);
            });
        }

        private readonly ConcurrentDictionary<string, Type> _knownMessageTypesCache = new ConcurrentDictionary<string, Type>();
        protected Type GetMappedMessageType(string messageType) {
            return _knownMessageTypesCache.GetOrAdd(messageType, type => {
                if (_options.MessageTypeMappings != null && _options.MessageTypeMappings.ContainsKey(type))
                    return _options.MessageTypeMappings[type];

                try {
                    return Type.GetType(type);
                } catch (Exception ex) {
                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning(ex, "Error getting message body type: {MessageType}", type);

                    return null;
                }
            });
        }

        protected virtual Task EnsureTopicSubscriptionAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        protected virtual Task SubscribeImplAsync<T>(Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken) where T : class {
            var subscriber = new Subscriber {
                CancellationToken = cancellationToken,
                Type = typeof(T),
                Action = (message, token) => {
                    if (!(message is T)) {
                        if (_logger.IsEnabled(LogLevel.Trace))
                            _logger.LogTrace("Unable to call subscriber action: {MessageType} cannot be safely casted to {SubscriberType}", message.GetType(), typeof(T));
                        return Task.CompletedTask;
                    }

                    return handler((T)message, cancellationToken);
                }
            };

            if (!_subscribers.TryAdd(subscriber.Id, subscriber) && _logger.IsEnabled(LogLevel.Error))
                _logger.LogError("Unable to add subscriber {SubscriberId}", subscriber.Id);

            return Task.CompletedTask;
        }

        public async Task SubscribeAsync<T>(Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken = default) where T : class {
            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("Adding subscriber for {MessageType}.", typeof(T).FullName);
            await EnsureTopicSubscriptionAsync(cancellationToken).AnyContext();
            await SubscribeImplAsync(handler, cancellationToken).AnyContext();
        }

        protected void SendMessageToSubscribers(MessageBusData message, ISerializer serializer) {
            var messageType = GetMessageBodyType(message);
            if (messageType == null)
                return;

            var subscribers = _subscribers.Values.Where(s => s.IsAssignableFrom(messageType)).ToList();
            if (subscribers.Count == 0) {
                if (_logger.IsEnabled(LogLevel.Trace))
                    _logger.LogTrace("Done sending message to 0 subscribers for message type {MessageType}.", messageType.Name);
                return;
            }

            object body;
            try {
                body = serializer.Deserialize(message.Data, messageType);
            } catch (Exception ex) {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning(ex, "Error deserializing message body: {Message}", ex.Message);
                return;
            }

            if (body == null) {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning("Unable to send null message for type {MessageType}", messageType.Name);
                return;
            }

            SendMessageToSubscribers(subscribers, messageType, body);
        }

        protected void SendMessageToSubscribers(List<Subscriber> subscribers, Type messageType, object message) {
            bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
            if (isTraceLogLevelEnabled)
                _logger.LogTrace("Found {SubscriberCount} subscribers for message type {MessageType}.", subscribers.Count, messageType.Name);

            foreach (var subscriber in subscribers) {
                if (subscriber.CancellationToken.IsCancellationRequested) {
                    if (_subscribers.TryRemove(subscriber.Id, out var _)) {
                        if (isTraceLogLevelEnabled)
                            _logger.LogTrace("Removed cancelled subscriber: {SubscriberId}", subscriber.Id);
                    } else if (isTraceLogLevelEnabled) {
                        _logger.LogTrace("Unable to remove cancelled subscriber: {SubscriberId}", subscriber.Id);
                    }

                    continue;
                }

                Task.Factory.StartNew(async () => {
                    if (subscriber.CancellationToken.IsCancellationRequested) {
                        if (isTraceLogLevelEnabled)
                            _logger.LogTrace("The cancelled subscriber action will not be called: {SubscriberId}", subscriber.Id);

                        return;
                    }

                    if (isTraceLogLevelEnabled)
                        _logger.LogTrace("Calling subscriber action: {SubscriberId}", subscriber.Id);

                    try {
                        await subscriber.Action(message, subscriber.CancellationToken).AnyContext();
                        if (isTraceLogLevelEnabled)
                            _logger.LogTrace("Finished calling subscriber action: {SubscriberId}", subscriber.Id);
                    } catch (Exception ex) {
                        if (_logger.IsEnabled(LogLevel.Warning))
                            _logger.LogWarning(ex, "Error sending message to subscriber: {Message}", ex.Message);
                    }
                });
            }

            if (isTraceLogLevelEnabled)
                _logger.LogTrace("Done enqueueing message to {SubscriberCount} subscribers for message type {MessageType}.", subscribers.Count, messageType.Name);
        }

        protected Type GetMessageBodyType(MessageBusData message) {
            if (message?.Type == null)
                return null;

            return GetMappedMessageType(message.Type);
        }

        protected void SendDelayedMessage(Type messageType, object message, TimeSpan delay) {
            if (message == null)
                throw new ArgumentNullException(nameof(message));
            
            if (delay <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(delay));

            var sendTime = SystemClock.UtcNow.SafeAdd(delay);
            Task.Factory.StartNew(async () => {
                await SystemClock.SleepSafeAsync(delay, _messageBusDisposedCancellationTokenSource.Token).AnyContext();

                bool isTraceLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
                if (_messageBusDisposedCancellationTokenSource.IsCancellationRequested) {
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

        public void Dispose() {
            if (_isDisposed) {
                _logger.LogWarning("MessageBus {0} dispose was already called.", MessageBusId);
                return;
            }
            
            _isDisposed = true;
            
            _logger.LogTrace("MessageBus {0} dispose", MessageBusId);
            _subscribers?.Clear();
            _messageBusDisposedCancellationTokenSource?.Cancel();
            _messageBusDisposedCancellationTokenSource?.Dispose();
        }

        [DebuggerDisplay("MessageType: {MessageType} SendTime: {SendTime} Message: {Message}")]
        protected class DelayedMessage {
            public DateTime SendTime { get; set; }
            public Type MessageType { get; set; }
            public object Message { get; set; }
        }

        [DebuggerDisplay("Id: {Id} Type: {Type} CancellationToken: {CancellationToken}")]
        protected class Subscriber {
            private readonly ConcurrentDictionary<Type, bool> _assignableTypesCache = new ConcurrentDictionary<Type, bool>();

            public string Id { get; private set; } = Guid.NewGuid().ToString();
            public CancellationToken CancellationToken { get; set; }
            public Type Type { get; set; }
            public Func<object, CancellationToken, Task> Action { get; set; }

            public bool IsAssignableFrom(Type type) {
                return _assignableTypesCache.GetOrAdd(type, t => Type.GetTypeInfo().IsAssignableFrom(t));
            }
        }
    }
}
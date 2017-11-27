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

namespace Foundatio.Messaging {
    public abstract class MessageBusBase<TOptions> : MaintenanceBase, IMessageBus where TOptions : MessageBusOptionsBase {
        protected readonly ConcurrentDictionary<string, Subscriber> _subscribers = new ConcurrentDictionary<string, Subscriber>();
        private readonly ConcurrentDictionary<string, Type> _knownMessageTypesCache = new ConcurrentDictionary<string, Type>();
        private readonly ConcurrentDictionary<Guid, DelayedMessage> _delayedMessages = new ConcurrentDictionary<Guid, DelayedMessage>();
        protected readonly TOptions _options;
        protected readonly ISerializer _serializer;

        public MessageBusBase(TOptions options) : base(options?.LoggerFactory) {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _serializer = options.Serializer ?? new JsonNetSerializer();
            MessageBusId = _options.Topic + Guid.NewGuid().ToString("N").Substring(10);

            InitializeMaintenance();
        }

        protected virtual Task EnsureTopicCreatedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        protected abstract Task PublishImplAsync(Type messageType, object message, TimeSpan? delay, CancellationToken cancellationToken);
        public async Task PublishAsync(Type messageType, object message, TimeSpan? delay = null, CancellationToken cancellationToken = default(CancellationToken)) {
            if (message == null)
                return;

            await EnsureTopicCreatedAsync(cancellationToken).AnyContext();
            await PublishImplAsync(messageType, message, delay, cancellationToken).AnyContext();
        }

        protected virtual Task EnsureTopicSubscriptionAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        protected virtual Task SubscribeImplAsync<T>(Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken) where T : class {
            var subscriber = new Subscriber {
                CancellationToken = cancellationToken,
                Type = typeof(T),
                Action = (message, token) => {
                    if (!(message is T))
                        return Task.CompletedTask;

                    return handler((T)message, cancellationToken);
                }
            };

            if (!_subscribers.TryAdd(subscriber.Id, subscriber) && _logger.IsEnabled(LogLevel.Error))
                _logger.LogError("Unable to add subscriber {SubscriberId}", subscriber.Id);

            return Task.CompletedTask;
        }

        public async Task SubscribeAsync<T>(Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken = default(CancellationToken)) where T : class {
            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("Adding subscriber for {MessageType}.", typeof(T).FullName);
            await EnsureTopicSubscriptionAsync(cancellationToken).AnyContext();
            await SubscribeImplAsync(handler, cancellationToken).AnyContext();
        }

        protected Task SendMessageToSubscribersAsync(MessageBusData message, ISerializer serializer) {
            var messageType = GetMessageBodyType(message);
            if (messageType == null)
                return Task.CompletedTask;

            var subscribers = _subscribers.Values.Where(s => s.IsAssignableFrom(messageType)).ToList();
            if (subscribers.Count == 0) {
                if (_logger.IsEnabled(LogLevel.Trace))
                    _logger.LogTrace("Done sending message to 0 subscribers for message type {MessageType}.", messageType.Name);
                return Task.CompletedTask;
            }

            object body;
            try {
                body = serializer.Deserialize(message.Data, messageType);
            } catch (Exception ex) {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning(ex, "Error deserializing messsage body: {Message}", ex.Message);
                return Task.CompletedTask;
            }

            if (body == null) {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning("Unable to send null message for type {MessageType}", messageType.Name);
                return Task.CompletedTask;
            }

            return SendMessageToSubscribersAsync(subscribers, messageType, body);
        }

        protected async Task SendMessageToSubscribersAsync(List<Subscriber> subscribers, Type messageType, object message) {
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

                try {
                    await subscriber.Action(message, subscriber.CancellationToken).AnyContext();
                } catch (Exception ex) {
                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning(ex, "Error sending message to subscriber: {Message}", ex.Message);
                }
            }

            if (isTraceLogLevelEnabled)
                _logger.LogTrace("Done sending message to {SubscriberCount} subscribers for message type {MessageType}.", subscribers.Count, messageType.Name);
        }

        protected Type GetMessageBodyType(MessageBusData message) {
            if (message?.Type == null)
                return null;

            return _knownMessageTypesCache.GetOrAdd(message.Type, type => {
                try {
                    return Type.GetType(type);
                } catch (Exception ex) {
                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning(ex, "Error getting message body type: {MessageType}", type);

                    return null;
                }
            });
        }

        protected Task AddDelayedMessageAsync(Type messageType, object message, TimeSpan delay) {
            if (message == null)
                throw new ArgumentNullException(nameof(message));

            var sendTime = SystemClock.UtcNow.Add(delay);
            _delayedMessages.TryAdd(Guid.NewGuid(), new DelayedMessage {
                Message = message,
                MessageType = messageType,
                SendTime = sendTime
            });

            ScheduleNextMaintenance(sendTime);
            return Task.CompletedTask;
        }

        protected override async Task<DateTime?> DoMaintenanceAsync() {
            if (_delayedMessages == null || _delayedMessages.Count == 0)
                return DateTime.MaxValue;

            var nextMessageSendTime = DateTime.MaxValue;
            var messagesToSend = new List<Guid>();

            // Add 50ms to the current time so we can batch up any other messages that will
            // happen very shortly. Also the timer may run earilier than requested.
            var sendTime = SystemClock.UtcNow.AddMilliseconds(50);
            foreach (var pair in _delayedMessages) {
                if (pair.Value.SendTime <= sendTime)
                    messagesToSend.Add(pair.Key);
                else if (pair.Value.SendTime < nextMessageSendTime)
                    nextMessageSendTime = pair.Value.SendTime;
            }

            bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
            foreach (var messageId in messagesToSend) {
                if (!_delayedMessages.TryRemove(messageId, out var message))
                    continue;

                if (isTraceLogLevelEnabled)
                    _logger.LogTrace("Sending delayed message scheduled for {SendTime} for type {MessageType}", message.SendTime.ToString("o"), message.MessageType);
                await PublishAsync(message.MessageType, message.Message).AnyContext();
            }

            if (isTraceLogLevelEnabled)
                _logger.LogTrace("DoMaintenance next message send time: {SendTime}", nextMessageSendTime.ToString("o"));
            return nextMessageSendTime;
        }

        public string MessageBusId { get; protected set; }

        public override void Dispose() {
            _logger.LogTrace("Disposing");
            base.Dispose();
            _delayedMessages?.Clear();
            _subscribers?.Clear();
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
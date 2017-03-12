using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Serializer;
using Foundatio.Utility;

namespace Foundatio.Messaging {
    public abstract class MessageBusBase : MaintenanceBase, IMessagePublisher {
        protected readonly ConcurrentDictionary<string, Subscriber> _subscribers = new ConcurrentDictionary<string, Subscriber>();
        private readonly ConcurrentDictionary<string, Type> _knownMessageTypesCache = new ConcurrentDictionary<string, Type>();
        private readonly ConcurrentDictionary<Guid, DelayedMessage> _delayedMessages = new ConcurrentDictionary<Guid, DelayedMessage>();

        public MessageBusBase(ILoggerFactory loggerFactory) : base(loggerFactory) {
            InitializeMaintenance();
        }

        public abstract Task PublishAsync(Type messageType, object message, TimeSpan? delay = null, CancellationToken cancellationToken = default(CancellationToken));


        protected async Task SendMessageToSubscribersAsync(MessageBusData message, ISerializer serializer) {
            Type messageType = GetMessageBodyType(message);
            if (messageType == null)
                return;

            var subscribers = _subscribers.Values.Where(s => s.IsAssignableFrom(messageType)).ToList();
            if (subscribers.Count == 0) {
                _logger.Trace(() => $"Done sending message to 0 subscribers for message type {messageType.Name}.");
                return;
            }

            object body;
            try {
                body = await serializer.DeserializeAsync(message.Data, messageType).AnyContext();
            } catch (Exception ex) {
                _logger.Error(ex, "Error while deserializing messsage body: {0}", ex.Message);
                return;
            }

            if (body == null) {
                _logger.Warn("Unable to send null message for type {0}", messageType.Name);
                return;
            }

            await SendMessageToSubscribersAsync(subscribers, messageType, body).AnyContext();
        }

        protected async Task SendMessageToSubscribersAsync(List<Subscriber> subscribers, Type messageType, object message) {
            _logger.Trace(() => $"Found {subscribers.Count} subscribers for message type {messageType.Name}.");
            foreach (var subscriber in subscribers) {
                if (subscriber.CancellationToken.IsCancellationRequested) {
                    if (_subscribers.TryRemove(subscriber.Id, out Subscriber sub))
                        _logger.Trace("Removed cancelled subscriber: {subscriberId}", subscriber.Id);
                    else
                        _logger.Trace("Unable to remove cancelled subscriber: {subscriberId}", subscriber.Id);

                    continue;
                }

                try {
                    await subscriber.Action(message, subscriber.CancellationToken).AnyContext();
                } catch (Exception ex) {
                    _logger.Error(ex, "Error sending message to subscriber: {0}", ex.Message);
                }
            }
            _logger.Trace(() => $"Done sending message to {subscribers.Count} subscribers for message type {messageType.Name}.");
        }

        public virtual Task SubscribeAsync<T>(Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken = default(CancellationToken)) where T : class {
            _logger.Trace("Adding subscriber for {0}.", typeof(T).FullName);
            var subscriber = new Subscriber {
                CancellationToken = cancellationToken,
                Type = typeof(T),
                Action = (message, token) => {
                    if (!(message is T))
                        return Task.CompletedTask;

                    return handler((T)message, cancellationToken);
                }
            };

            if (!_subscribers.TryAdd(subscriber.Id, subscriber))
                _logger.Error("Unable to add subscriber {subscriberId}", subscriber.Id);

            return Task.CompletedTask;
        }

        protected Type GetMessageBodyType(MessageBusData message) {
            if (message?.Type == null)
                return null;

            return _knownMessageTypesCache.GetOrAdd(message.Type, type => {
                try {
                    return Type.GetType(type);
                } catch (Exception ex) {
                    _logger.Error(ex, "Error getting message body type: {0}", type);
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

            DateTime nextMessageSendTime = DateTime.MaxValue;
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

            foreach (var messageId in messagesToSend) {
                if (!_delayedMessages.TryRemove(messageId, out DelayedMessage message))
                    continue;

                _logger.Trace("Sending delayed message scheduled for {0} for type {1}", message.SendTime.ToString("o"), message.MessageType);
                await PublishAsync(message.MessageType, message.Message).AnyContext();
            }

            _logger.Trace("DoMaintenance next message send time: {0}", nextMessageSendTime.ToString("o"));
            return nextMessageSendTime;
        }

        public override void Dispose() {
            base.Dispose();
            _delayedMessages?.Clear();
            _subscribers?.Clear();
        }

        protected class DelayedMessage {
            public DateTime SendTime { get; set; }
            public Type MessageType { get; set; }
            public object Message { get; set; }
        }
        
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
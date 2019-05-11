using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Foundatio.Serializer;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging {
    public class InMemoryMessageBus : MessageBusBase<InMemoryMessageBusOptions> {
        private readonly ConcurrentDictionary<string, long> _messageCounts = new ConcurrentDictionary<string, long>();
        private long _messagesSent;
        private readonly TaskFactory _taskFactory;

        public InMemoryMessageBus() : this(o => o) {}

        public InMemoryMessageBus(InMemoryMessageBusOptions options) : base(options) {
            // limit message processing to 50 at a time
            _taskFactory = new TaskFactory(new LimitedConcurrencyLevelTaskScheduler(50));
        }

        public InMemoryMessageBus(Builder<InMemoryMessageBusOptionsBuilder, InMemoryMessageBusOptions> config)
            : this(config(new InMemoryMessageBusOptionsBuilder()).Build()) { }

        public long MessagesSent => _messagesSent;

        public long GetMessagesSent(Type messageType) {
            return _messageCounts.TryGetValue(_typeNameSerializer.Serialize(messageType), out var count) ? count : 0;
        }

        public long GetMessagesSent<T>() {
            return _messageCounts.TryGetValue(_typeNameSerializer.Serialize(typeof(T)), out var count) ? count : 0;
        }

        public void ResetMessagesSent() {
            Interlocked.Exchange(ref _messagesSent, 0);
            _messageCounts.Clear();
        }

        protected override Task PublishImplAsync(byte[] body, MessagePublishOptions options = null) {
            Interlocked.Increment(ref _messagesSent);
            var typeName = _typeNameSerializer.Serialize(options.MessageType);
            _messageCounts.AddOrUpdate(typeName, t => 1, (t, c) => c + 1);

            if (_subscriptions.Count == 0)
                return Task.CompletedTask;

            _logger.LogTrace("Message Publish: {MessageType}", options.MessageType.FullName);

            SendMessageToSubscribers(body, options);
            return Task.CompletedTask;
        }

        protected override Task<IMessageSubscription> SubscribeImplAsync(MessageSubscriptionOptions options, Func<IMessageContext, Task> handler) {
            var subscriber = new Subscriber(options.MessageType, handler);
            return Task.FromResult<IMessageSubscription>(subscriber);
        }

        protected void SendMessageToSubscribers(byte[] body, MessagePublishOptions options) {
            if (body == null)
                throw new ArgumentNullException(nameof(body));
            
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            
            var createdUtc = SystemClock.UtcNow;
            var messageId = Guid.NewGuid().ToString();
            Func<object> getBody = () => {
                return _serializer.Deserialize(body, options.MessageType);
            };

            var subscribers = _subscriptions.ToArray().Where(s => s.IsCancelled == false && s.HandlesMessagesType(options.MessageType)).OfType<Subscriber>().ToArray();
            bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
            if (isTraceLogLevelEnabled)
                _logger.LogTrace("Found {SubscriberCount} subscribers for message type {MessageType}.", subscribers.Length, options.MessageType.Name);

            foreach (var subscriber in subscribers) {
                _taskFactory.StartNew(async () => {
                    if (subscriber.IsCancelled) {
                        if (isTraceLogLevelEnabled)
                            _logger.LogTrace("The cancelled subscriber action will not be called: {SubscriberId}", subscriber.Id);

                        return;
                    }

                    if (isTraceLogLevelEnabled)
                        _logger.LogTrace("Calling subscriber action: {SubscriberId}", subscriber.Id);

                    try {
                        var message = new Message(getBody, options.MessageType, options.CorrelationId, options.ExpiresAtUtc, options.DeliverAtUtc, options.Properties);
                        var context = new MessageContext(messageId, subscriber.Id, createdUtc, 1, message, () => Task.CompletedTask, () => Task.CompletedTask, () => {}, options.CancellationToken);
                        await subscriber.Action(context).AnyContext();
                        if (isTraceLogLevelEnabled)
                            _logger.LogTrace("Finished calling subscriber action: {SubscriberId}", subscriber.Id);
                    } catch (Exception ex) {
                        if (_logger.IsEnabled(LogLevel.Warning))
                            _logger.LogWarning(ex, "Error sending message to subscriber: {Message}", ex.Message);
                    }
                });
            }

            if (isTraceLogLevelEnabled)
                _logger.LogTrace("Done enqueueing message to {SubscriberCount} subscribers for message type {MessageType}.", subscribers.Length, options.MessageType.Name);
        }

        [DebuggerDisplay("Id: {Id} Type: {MessageType} IsDisposed: {IsDisposed}")]
        protected class Subscriber : MessageSubscription {
            public Subscriber(Type messageType, Func<IMessageContext, Task> action) : base(messageType, () => {}) {
                Action = action;
            }

            public Func<IMessageContext, Task> Action { get; }
        }
    }
}
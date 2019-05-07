using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging {
    public class InMemoryMessageBus : MessageBusBase<InMemoryMessageBusOptions> {
        private readonly ConcurrentDictionary<string, long> _messageCounts = new();
        private long _messagesSent;

        public InMemoryMessageBus() : this(o => o) {}

        public InMemoryMessageBus(InMemoryMessageBusOptions options) : base(options) { }

        public InMemoryMessageBus(Builder<InMemoryMessageBusOptionsBuilder, InMemoryMessageBusOptions> config)
            : this(config(new InMemoryMessageBusOptionsBuilder()).Build()) { }

        public long MessagesSent => _messagesSent;

        public long GetMessagesSent(Type messageType) {
            return _messageCounts.TryGetValue(GetMappedMessageType(messageType), out long count) ? count : 0;
        }

        public long GetMessagesSent<T>() {
            return _messageCounts.TryGetValue(GetMappedMessageType(typeof(T)), out long count) ? count : 0;
        }

        public void ResetMessagesSent() {
            Interlocked.Exchange(ref _messagesSent, 0);
            _messageCounts.Clear();
        }

        protected override Task PublishImplAsync(Type messageType, object message, TimeSpan? delay, CancellationToken cancellationToken) {
            Interlocked.Increment(ref _messagesSent);
            var mappedMessageType = GetMappedMessageType(messageType);
            _messageCounts.AddOrUpdate(mappedMessageType, t => 1, (t, c) => c + 1);

            if (_subscribers.IsEmpty)
                return;

            bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
            if (options.DeliveryDelay.HasValue && options.DeliveryDelay.Value > TimeSpan.Zero) {
                if (isTraceLogLevelEnabled)
                    _logger.LogTrace("Schedule delayed message: {MessageType} ({Delay}ms)", messageType, delay.Value.TotalMilliseconds);
                SendDelayedMessage(messageType, message, delay.Value);
                return Task.CompletedTask;
            }
            
            byte[] body = SerializeMessageBody(messageType, message);
            var messageData = new Message(() => DeserializeMessageBody(messageType, body)) {
                Type = messageType,
                ClrType = mappedType,
                Data = body
            };

            var subscribers = _subscribers.Values.Where(s => s.IsAssignableFrom(messageType)).ToList();
            if (subscribers.Count == 0) {
                if (isTraceLogLevelEnabled)
                    _logger.LogTrace("Done sending message to 0 subscribers for message type {MessageType}.", messageType.Name);
                return Task.CompletedTask;
            }

            if (isTraceLogLevelEnabled)
                _logger.LogTrace("Message Publish: {MessageType}", messageType.FullName);

            SendMessageToSubscribers(subscribers, messageType, message.DeepClone());
            return Task.CompletedTask;
        }
    }
}
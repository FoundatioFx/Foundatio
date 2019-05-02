using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging {
    public class InMemoryMessageBus : MessageBusBase<InMemoryMessageBusOptions> {
        private readonly ConcurrentDictionary<string, long> _messageCounts = new ConcurrentDictionary<string, long>();
        private long _messagesSent;

        public InMemoryMessageBus() : this(o => o) {}

        public InMemoryMessageBus(InMemoryMessageBusOptions options) : base(options) { }

        public InMemoryMessageBus(Builder<InMemoryMessageBusOptionsBuilder, InMemoryMessageBusOptions> config)
            : this(config(new InMemoryMessageBusOptionsBuilder()).Build()) { }

        public long MessagesSent => _messagesSent;

        public long GetMessagesSent(Type messageType) {
            return _messageCounts.TryGetValue(GetMappedMessageType(messageType), out var count) ? count : 0;
        }

        public long GetMessagesSent<T>() {
            return _messageCounts.TryGetValue(GetMappedMessageType(typeof(T)), out var count) ? count : 0;
        }

        public void ResetMessagesSent() {
            Interlocked.Exchange(ref _messagesSent, 0);
            _messageCounts.Clear();
        }

        protected override Task PublishImplAsync(string messageType, object message, TimeSpan? delay, CancellationToken cancellationToken) {
            Interlocked.Increment(ref _messagesSent);
            _messageCounts.AddOrUpdate(messageType, t => 1, (t, c) => c + 1);
            Type mappedType = GetMappedMessageType(messageType);

            if (_subscribers.IsEmpty)
                return Task.CompletedTask;

            bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
            if (delay.HasValue && delay.Value > TimeSpan.Zero) {
                if (isTraceLogLevelEnabled)
                    _logger.LogTrace("Schedule delayed message: {MessageType} ({Delay}ms)", messageType, delay.Value.TotalMilliseconds);
                SendDelayedMessage(mappedType, message, delay.Value);
                return Task.CompletedTask;
            }

            var subscribers = _subscribers.Values.Where(s => s.IsAssignableFrom(mappedType, message)).ToList();
            if (subscribers.Count == 0) {
                if (isTraceLogLevelEnabled)
                    _logger.LogTrace("Done sending message to 0 subscribers for message type {MessageType}.", mappedType.Name);
                return Task.CompletedTask;
            }

            if (isTraceLogLevelEnabled)
                _logger.LogTrace("Message Publish: {MessageType}", mappedType.FullName);

            SendMessageToSubscribers(subscribers, mappedType, message.DeepClone());
            return Task.CompletedTask;
        }
    }
}
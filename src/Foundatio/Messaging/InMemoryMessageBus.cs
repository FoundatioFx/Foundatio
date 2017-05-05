using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Foundatio.Logging;

namespace Foundatio.Messaging {
    public class InMemoryMessageBus : MessageBusBase<InMemoryMessageBusOptions> {
        private readonly ConcurrentDictionary<Type, long> _messageCounts = new ConcurrentDictionary<Type, long>();
        private long _messagesSent;

        [Obsolete("Use the options overload")]
        public InMemoryMessageBus(ILoggerFactory loggerFactory = null) : this(new InMemoryMessageBusOptions { LoggerFactory = loggerFactory }) { }

        public InMemoryMessageBus(InMemoryMessageBusOptions options) : base(options) { }

        public long MessagesSent => _messagesSent;

        public long GetMessagesSent(Type messageType) {
            return _messageCounts.TryGetValue(messageType, out long count) ? count : 0;
        }

        public long GetMessagesSent<T>() {
            return _messageCounts.TryGetValue(typeof(T), out long count) ? count : 0;
        }

        public void ResetMessagesSent() {
            Interlocked.Exchange(ref _messagesSent, 0);
            _messageCounts.Clear();
        }

        protected override Task PublishImplAsync(Type messageType, object message, TimeSpan? delay, CancellationToken cancellationToken) {
            Interlocked.Increment(ref _messagesSent);
            _messageCounts.AddOrUpdate(messageType, t => 1, (t, c) => c + 1);

            if (_subscribers.IsEmpty)
                return Task.CompletedTask;

            if (delay.HasValue && delay.Value > TimeSpan.Zero) {
                _logger.Trace("Schedule delayed message: {messageType} ({delay}ms)", messageType.FullName, delay.Value.TotalMilliseconds);
                return AddDelayedMessageAsync(messageType, message, delay.Value);
            }

            var subscribers = _subscribers.Values.Where(s => s.IsAssignableFrom(messageType)).ToList();
            if (subscribers.Count == 0) {
                _logger.Trace(() => $"Done sending message to 0 subscribers for message type {messageType.Name}.");
                return Task.CompletedTask;
            }

            _logger.Trace("Message Publish: {messageType}", messageType.FullName);

            return SendMessageToSubscribersAsync(subscribers, messageType, message.DeepClone());
        }
    }
}
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;

namespace Foundatio.Messaging {
    public class InMemoryMessageBus : MessageBusBase<InMemoryMessageBusOptions> {
        [Obsolete("Use the options overload")]
        public InMemoryMessageBus(ILoggerFactory loggerFactory = null) : this(new InMemoryMessageBusOptions { LoggerFactory = loggerFactory }) { }

        public InMemoryMessageBus(InMemoryMessageBusOptions options) : base(options) { }

        protected override Task PublishImplAsync(Type messageType, object message, TimeSpan? delay, CancellationToken cancellationToken) {
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
            return SendMessageToSubscribersAsync(subscribers, messageType, message.Copy());
        }
    }
}
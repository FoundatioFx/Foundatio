using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;

namespace Foundatio.Messaging {
    public class InMemoryMessageBus : MessageBusBase, IMessageBus {
        public InMemoryMessageBus(ILoggerFactory loggerFactory = null) : base(loggerFactory) {}

        public override Task PublishAsync(Type messageType, object message, TimeSpan? delay = null, CancellationToken cancellationToken = default(CancellationToken)) {
            if (message == null || _subscribers.IsEmpty)
                return Task.CompletedTask;

            if (delay.HasValue && delay.Value > TimeSpan.Zero)
                return AddDelayedMessageAsync(messageType, message, delay.Value);

            var subscribers = _subscribers.Values.Where(s => s.IsAssignableFrom(messageType)).ToList();
            if (subscribers.Count == 0) {
                _logger.Trace(() => $"Done sending message to 0 subscribers for message type {messageType.Name}.");
                return Task.CompletedTask;
            }

            return SendMessageToSubscribersAsync(subscribers, messageType, message.Copy());
        }
    }
}
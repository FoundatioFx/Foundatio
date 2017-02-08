using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;

namespace Foundatio.Messaging {
    public class InMemoryMessageBus : MessageBusBase, IMessageBus {
        public InMemoryMessageBus(ILoggerFactory loggerFactory = null) : base(loggerFactory) {}

        public override Task PublishAsync(Type messageType, object message, TimeSpan? delay = null, CancellationToken cancellationToken = default(CancellationToken)) {
            if (message == null)
                return Task.CompletedTask;

            if (delay.HasValue && delay.Value > TimeSpan.Zero)
                return AddDelayedMessageAsync(messageType, message, delay.Value);

            return SendMessageToSubscribersAsync(messageType, message.Copy());
        }
    }
}
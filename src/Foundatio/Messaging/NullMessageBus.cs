using System;
using System.Threading.Tasks;

namespace Foundatio.Messaging {
    public class NullMessageBus : IMessageBus {
        public static readonly NullMessageBus Instance = new();

        public string MessageBusId { get; } = Guid.NewGuid().ToString("N");

        public Task PublishAsync(object message, MessagePublishOptions options = null) {
            return Task.CompletedTask;
        }

        public Task<IMessageSubscription> SubscribeAsync(MessageSubscriptionOptions options, Func<IMessageContext, Task> handler) {
            return Task.FromResult<IMessageSubscription>(new MessageSubscription(options.MessageType, () => {}));
        }

        public Task<IMessageContext> ReceiveAsync(MessageReceiveOptions options) {
            return Task.FromResult<IMessageContext>(null);
        }

        public void Dispose() {}
    }
}

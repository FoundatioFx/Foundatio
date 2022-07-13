using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Messaging {
    public class NullMessageBus : IMessageBus {
        public static readonly NullMessageBus Instance = new();

        public Task PublishAsync(Type messageType, object message, MessageOptions options = null, CancellationToken cancellationToken = default) {
            return Task.CompletedTask;
        }

        public Task SubscribeAsync<T>(Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken = default) where T : class {
            return Task.CompletedTask;
        }

        public void Dispose() {}

        public Task<IMessageSubscription> SubscribeAsync<T>(Func<T, CancellationToken, Task> handler, MessageSubscriptionOptions options = null) where T : class {
            return Task.FromResult<IMessageSubscription>(new MessageSubscription(Guid.NewGuid().ToString(), () => new ValueTask()));
        }
    }
}

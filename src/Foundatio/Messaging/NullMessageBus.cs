using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Messaging {
    public class NullMessageBus : IMessageBus {
        public static readonly NullMessageBus Instance = new NullMessageBus();

        public Task PublishAsync(Type messageType, object message, TimeSpan? delay = null, CancellationToken cancellationToken = default) {
            return Task.CompletedTask;
        }

        public Task<IMessageSubscription> SubscribeAsync<T>(Func<T, CancellationToken, Task> handler) where T : class {
            return Task.FromResult<IMessageSubscription>(new NullMessageSubscription());
        }

        public void Dispose() {}
    }

    public class NullMessageSubscription : IMessageSubscription {
        public void Dispose() {}
    }
}

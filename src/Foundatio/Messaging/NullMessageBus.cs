using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Queues;

namespace Foundatio.Messaging {
    public class NullMessageBus : IMessageBus {
        public static readonly NullMessageBus Instance = new NullMessageBus();

        public Task PublishAsync(Type messageType, object message, TimeSpan? delay = null, QueueEntryOptions options = null, CancellationToken cancellationToken = default) {
            return Task.CompletedTask;
        }

        public Task SubscribeAsync<T>(Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken = default) where T : class {
            return Task.CompletedTask;
        }

        public void Dispose() {}
    }
}

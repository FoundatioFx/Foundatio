using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Messaging {
    public class NullMessageBus : IMessageBus {
        public static readonly NullMessageBus Instance = new NullMessageBus();

        public Task PublishAsync(Type messageType, object message, TimeSpan? delay = null, CancellationToken cancellationToken = default) {
            return Task.CompletedTask;
        }

        public Task SubscribeAsync<T>(Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken = default, Func<T, bool> messagefilter = null) where T : class {
            return Task.CompletedTask;
        }
        
        public void Dispose() {}
    }
}

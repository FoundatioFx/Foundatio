using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Messaging
{
    public class NullMessageBus : IMessageBus
    {
        public static readonly NullMessageBus Instance = new();

        public Task PublishAsync(Type messageType, object message, MessageOptions options = null, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SubscribeAsync<T>(Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken = default) where T : class
        {
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }

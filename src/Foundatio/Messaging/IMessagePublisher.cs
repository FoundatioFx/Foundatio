using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Queues;

namespace Foundatio.Messaging {
    public interface IMessagePublisher {
        Task PublishAsync(Type messageType, object message, TimeSpan? delay = null, MessageOptions options = null, CancellationToken cancellationToken = default);
    }

    public static class MessagePublisherExtensions {
        public static Task PublishAsync<T>(this IMessagePublisher publisher, T message, TimeSpan? delay = null, MessageOptions options = null) where T : class {
            return publisher.PublishAsync(typeof(T), message, delay, options);
        }
    }
}

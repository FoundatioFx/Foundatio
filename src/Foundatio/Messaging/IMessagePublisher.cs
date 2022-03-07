using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Messaging {
    public interface IMessagePublisher {
        Task PublishAsync(Type messageType, object message, MessageOptions options = null, CancellationToken cancellationToken = default);
    }

    public static class MessagePublisherExtensions {
        public static Task PublishAsync<T>(this IMessagePublisher publisher, T message, MessageOptions options = null) where T : class {
            return publisher.PublishAsync(typeof(T), message, options);
        }

        public static Task PublishAsync<T>(this IMessagePublisher publisher, T message, TimeSpan delay, CancellationToken cancellationToken = default) where T : class {
            return publisher.PublishAsync(typeof(T), message, new MessageOptions { DeliveryDelay = delay }, cancellationToken);
        }
    }
}

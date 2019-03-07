using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Messaging {
    public interface IMessagePublisher {
        // extensions for easily publishing just the raw message body, message settings populated from conventions
        Task PublishAsync(IMessage message);
    }

    public static class MessagePublisherExtensions {
        public static Task PublishAsync<T>(this IMessagePublisher publisher, T message) {
            var m = new Message<T>();
            return publisher.PublishAsync(m);
        }

        public static Task PublishAsync<T>(this IMessagePublisher publisher, T message, TimeSpan? delay = null) where T : class {
            return publisher.PublishAsync(typeof(T), message, delay);
        }
    }
}

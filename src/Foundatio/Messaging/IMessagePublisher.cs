using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Messaging {
    public interface IMessagePublisher {
        Task PublishAsync(object message, MessagePublishOptions options);
    }

    public class MessagePublishOptions {
        public Type MessageType { get; set; }
        public string CorrelationId { get; set; }
        public DateTime? ExpiresAtUtc { get; set; }
        public DateTime? DeliverAtUtc { get; set; }
        public DataDictionary Headers { get; set; } = new DataDictionary();
        public CancellationToken CancellationToken { get; set; }

        public MessagePublishOptions WithMessageType(Type messageType) {
            MessageType = messageType;
            return this;
        }

        public MessagePublishOptions WithCorrelationId(string correlationId) {
            CorrelationId = correlationId;
            return this;
        }

        public MessagePublishOptions WithExpiresAtUtc(DateTime? expiresAtUtc) {
            ExpiresAtUtc = expiresAtUtc;
            return this;
        }

        public MessagePublishOptions WithDeliverAtUtc(DateTime? deliverAtUtc) {
            DeliverAtUtc = deliverAtUtc;
            return this;
        }

        public MessagePublishOptions WithHeaders(DataDictionary headers) {
            Headers.AddRange(headers);
            return this;
        }

        public MessagePublishOptions WithHeader(string name, object value) {
            Headers.Add(name, value);
            return this;
        }

        public MessagePublishOptions WithCancellationToken(CancellationToken cancellationToken) {
            CancellationToken = cancellationToken;
            return this;
        }
    }

    public static class MessagePublisherExtensions {
        public static Task PublishAsync(this IMessagePublisher publisher, Type messageType, object message, TimeSpan? delay = null, CancellationToken cancellationToken = default) {
            var deliverAtUtc = delay.HasValue ? (DateTime?)DateTime.UtcNow.Add(delay.Value) : null;
            return publisher.PublishAsync(message, new MessagePublishOptions().WithMessageType(messageType).WithDeliverAtUtc(deliverAtUtc).WithCancellationToken(cancellationToken));
        }

        public static Task PublishAsync<T>(this IMessagePublisher publisher, T message, TimeSpan? delay = null) where T : class {
            return publisher.PublishAsync(typeof(T), message, delay);
        }

        public static Task PublishAsync<T>(this IMessagePublisher publisher, T message, MessagePublishOptions options) where T : class {
            if (options == null)
                options = new MessagePublishOptions();
            
            if (options.MessageType == null)
                options.MessageType = typeof(T);

            return publisher.PublishAsync(message, options);
        }
    }
}

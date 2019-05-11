using System;
using System.Collections.Generic;

namespace Foundatio.Messaging {
    public interface IMessage {
        // correlation id used in logging
        string CorrelationId { get; }
        // message type, will be converted to string and stored with the message for deserialization
        Type MessageType { get; }
        // message body
        object GetBody();
        // when the message should expire
        DateTime? ExpiresAtUtc { get; }
        // when the message should be delivered when using delayed delivery
        DateTime? DeliverAtUtc { get; }
        // additional message data to store with the message
        IReadOnlyDictionary<string, string> Properties { get; }
    }

    public class Message : IMessage {
        private Lazy<object> _body;

        public Message(Func<object> getBodyFunc, Type messageType, string coorelationId, DateTime? expiresAtUtc, DateTime? deliverAtUtc, IReadOnlyDictionary<string, string> properties) {
            _body = new Lazy<object>(getBodyFunc);
            MessageType = messageType;
            CorrelationId = coorelationId;
            ExpiresAtUtc = expiresAtUtc;
            DeliverAtUtc = deliverAtUtc;
            Properties = properties;
        }

        public Message(Func<object> getBodyFunc, MessagePublishOptions options) {
            _body = new Lazy<object>(getBodyFunc);
            CorrelationId = options.CorrelationId;
            MessageType = options.MessageType;
            ExpiresAtUtc = options.ExpiresAtUtc;
            DeliverAtUtc = options.DeliverAtUtc;
            Properties = options.Properties;
        }

        public string CorrelationId { get; private set; }
        public Type MessageType { get; private set; }
        public DateTime? ExpiresAtUtc { get; private set; }
        public DateTime? DeliverAtUtc { get; private set; }
        public IReadOnlyDictionary<string, string> Properties { get; private set; }
        
        public object GetBody() {
            return _body.Value;
        }
    }

    public interface IMessage<out T> : IMessage where T: class {
        T Body { get; }
    }

    public class Message<T> : IMessage<T> where T: class {
        private readonly IMessage _message;

        public Message(IMessage message) {
            _message = message;
        }

        public T Body => (T)GetBody();

        public string CorrelationId => _message.CorrelationId;
        public Type MessageType => _message.MessageType;
        public DateTime? ExpiresAtUtc => _message.ExpiresAtUtc;
        public DateTime? DeliverAtUtc => _message.DeliverAtUtc;
        public IReadOnlyDictionary<string, string> Properties => _message.Properties;
        public object GetBody() => _message.GetBody();
    }
}

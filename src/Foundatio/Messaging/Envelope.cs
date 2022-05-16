using System;
using System.Collections.Generic;

namespace Foundatio.Messaging2 {
    public interface IEnvelope {
        // trace parent id used for distributed tracing
        string TraceParentId { get; }
        // message type
        string MessageType { get; }
        // message body
        object GetMessage();
        // number of attempts to deliver the message
        int Attempts { get; }
        // when the message was originally sent
        DateTime SentAtUtc { get; }
        // when the message should expire
        DateTime? ExpiresAtUtc { get; }
        // when the message should be delivered when using delayed delivery
        DateTime? DeliverAtUtc { get; }
        // additional message data to store with the message
        IReadOnlyDictionary<string, string> Properties { get; }
    }
    
    public class Envelope : IEnvelope {
        private Lazy<object> _message;

        public Envelope(Func<object> getMessageFunc, string messageType, string coorelationId, DateTime? expiresAtUtc, DateTime? deliverAtUtc, IReadOnlyDictionary<string, string> properties) {
            _message = new Lazy<object>(getMessageFunc);
            MessageType = messageType;
            TraceParentId = coorelationId;
            ExpiresAtUtc = expiresAtUtc;
            DeliverAtUtc = deliverAtUtc;
            Properties = properties;
        }

        public Message(Func<object> getMessageFunc, MessagePublishOptions options) {
            _message = new Lazy<object>(getMessageFunc);
            TraceParentId = options.CorrelationId;
            MessageType = options.MessageType;
            ExpiresAtUtc = options.ExpiresAtUtc;
            DeliverAtUtc = options.DeliverAtUtc;
            Properties = options.Properties;
        }

        public string TraceParentId { get; private set; }
        public string MessageType { get; private set; }
        public int Attempts { get; private set; }
        public DateTime SentAtUtc { get; private set; }
        public DateTime? ExpiresAtUtc { get; private set; }
        public DateTime? DeliverAtUtc { get; private set; }
        public IReadOnlyDictionary<string, string> Properties { get; private set; }
        
        public object GetMessage() {
            return _message.Value;
        }
    }

    public interface IEnvelope<out T> : IEnvelope where T: class {
        T Message { get; }
    }

    public class Envelope<T> : IEnvelope<T> where T: class {
        private readonly IEnvelope _envolope;

        public Envelope(IEnvelope message) {
            _envolope = message;
        }

        public T Message => (T)GetMessage();

        public string TraceParentId => _envolope.TraceParentId;
        public string MessageType => _envolope.MessageType;
        public int Attempts => _envolope.Attempts;
        public DateTime SentAtUtc => _envolope.SentAtUtc;
        public DateTime? ExpiresAtUtc => _envolope.ExpiresAtUtc;
        public DateTime? DeliverAtUtc => _envolope.DeliverAtUtc;
        public IReadOnlyDictionary<string, string> Properties => _envolope.Properties;
        public object GetMessage() => _envolope.GetMessage();
    }
}

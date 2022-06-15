using System;
using System.Collections.Generic;
using System.Diagnostics;
using Foundatio.Serializer;

namespace Foundatio.Messaging {
    public interface IMessage {
        string UniqueId { get; }
        string CorrelationId { get; }
        string Type { get; }
        Type ClrType { get; }
        byte[] Data { get; }
        object GetBody();
        IDictionary<string, string> Properties { get; }
    }

    public interface IMessage<T> : IMessage where T: class {
        T Body { get; }
    }

    [DebuggerDisplay("Type: {Type}")]
    public class Message : IMessage {
        private readonly Func<IMessage, object> _getBody;

        public Message(byte[] data, Func<IMessage, object> getBody) {
            Data = data;
            _getBody = getBody;
        }

        public string UniqueId { get; set; }
        public string CorrelationId { get; set; }
        public string Type { get; set; }
        public Type ClrType { get; set; }
        public IDictionary<string, string> Properties { get; set; } = new Dictionary<string, string>();
        public byte[] Data { get; set; }
        public object GetBody() => _getBody(this);
    }

    public class Message<T> : IMessage<T> where T : class {
        private readonly IMessage _message;

        public Message(IMessage message) {
            _message = message;
        }

        public byte[] Data => _message.Data;

        public T Body => (T)GetBody();

        public string UniqueId => _message.UniqueId;

        public string CorrelationId => _message.CorrelationId;

        public string Type => _message.Type;

        public Type ClrType => _message.ClrType;

        public IDictionary<string, string> Properties => _message.Properties;

        public object GetBody() => _message.GetBody();
    }
}
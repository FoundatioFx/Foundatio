using System;
using System.Collections.Generic;
using Foundatio.Utility;

namespace Foundatio.Messaging {
    public interface IMessage {
        string Type { get; }
        Type ClrType { get; }
        byte[] Data { get; }
        object GetBody();
        IReadOnlyDictionary<string, string> Properties { get; }
    }

    public class Message : IMessage {
        private Lazy<object> _getBody;

        public Message(Func<object> getBody) {
            _getBody = new Lazy<object>(getBody);
        }

        public string Type { get; set; }
        public Type ClrType { get; set; }
        public byte[] Data { get; set; }
        public object GetBody() => _getBody.Value;
        public IReadOnlyDictionary<string, string> Properties { get; set; } = new Dictionary<string, string>();
    }
}
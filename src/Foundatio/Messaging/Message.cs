using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Foundatio.Messaging {
    public interface IMessage {
        string Type { get; }
        Type ClrType { get; }
        byte[] Data { get; }
        object GetBody();
        IReadOnlyDictionary<string, string> Properties { get; }
    }

    [DebuggerDisplay("Type: {Type}")]
    public class Message : IMessage {
        private readonly Lazy<object> _getBody;

        public Message(Func<IMessage, object> getBody) {
            if (getBody == null)
                throw new ArgumentNullException(nameof(getBody));

            _getBody = new Lazy<object>(() => getBody(this));
        }

        public string Type { get; set; }
        public Type ClrType { get; set; }
        public byte[] Data { get; set; }
        public object GetBody() => _getBody.Value;
        public IReadOnlyDictionary<string, string> Properties { get; set; } = new Dictionary<string, string>();
    }
}
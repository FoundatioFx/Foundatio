using System;
using System.Collections.Generic;

namespace Foundatio.Messaging {
    public interface IMessageBus : IMessagePublisher, IMessageSubscriber, IDisposable {}
    
    public class MessageOptions {
        public string UniqueId { get; set; }
        public string CorrelationId { get; set; }
        public string Topic { get; set; }
        public string MessageType { get; set; }
        public TimeSpan? DeliveryDelay { get; set; }
        public IDictionary<string, string> Properties { get; set; } = new Dictionary<string, string>();
    }
}
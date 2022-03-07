using System;
using Foundatio.Utility;

namespace Foundatio.Messaging {
    public interface IMessageBus : IMessagePublisher, IMessageSubscriber, IDisposable {}
    
    public class MessageOptions {
        public string UniqueId { get; set; }
        public string CorrelationId { get; set; }
        public TimeSpan? DeliveryDelay { get; set; }
        public DataDictionary Properties { get; set; } = new DataDictionary();
    }
}
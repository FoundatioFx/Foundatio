using System;
using Foundatio.Utility;

namespace Foundatio.Messaging {
    // work items go away, queues go away. everything consolidated down to just messaging
    // will be easy to handle random messages like you can with work items currently
    // conventions to determine message queue based on message type
    // conventions for determining default time to live, retries, and deadletter behavior based on message type, whether its a worker type or not
    // pub/sub you would publish messages and each subscriber would automatically get a unique subscriber id and the messages would go to all of them
    // worker you would publish messages and each subscriber would use the same subscriber id and the messages would get round robin'd
    // need to figure out how we would handle rpc request / response. need to be able to subscribe for a single message

    public interface IMessageBus : IMessagePublisher, IMessageSubscriber, IDisposable {}
    
    public class MessageOptions {
        public string UniqueId { get; set; }
        public string CorrelationId { get; set; }
        public TimeSpan? DeliveryDelay { get; set; }
        public DataDictionary Properties { get; set; } = new DataDictionary();
    }
}
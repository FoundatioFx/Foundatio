using System;

namespace Foundatio.Messaging {
    public interface IMessageBus : IMessagePublisher, IMessageSubscriber, IDisposable {}
    
    public class MessageOptions {
        public string UniqueId { get; set; }
    }

}
using System;

namespace Foundatio.Messaging {
    public interface IMessageBus : IMessagePublisher, IMessageSubscriber, IDisposable {
        string MessageBusId { get; }
    }
}
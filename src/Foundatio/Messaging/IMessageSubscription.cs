using System;

namespace Foundatio.Messaging {
    public interface IMessageSubscription : IDisposable {
        string Id { get; }
        string MessageBusId { get; }
        Type MessageType { get; }
        DateTime CreatedUtc { get; }
        bool IsCancelled { get; }
    }

    public static class MessageSubscriptionExtensions {
        public static bool HandlesMessagesType(this IMessageSubscription subscription, Type type) {
            return subscription.MessageType.IsAssignableFrom(type);
        }
    }
    
    public class MessageSubscription : IMessageSubscription {
        private readonly Action _unsubscribeAction;

        public MessageSubscription(Type messageType, Action unsubscribeAction) {
            Id = Guid.NewGuid().ToString("N");
            MessageType = messageType;
            CreatedUtc = DateTime.UtcNow;
            _unsubscribeAction = unsubscribeAction;
        }

        public string Id { get; }
        public string MessageBusId { get; }
        public Type MessageType { get; }
        public DateTime CreatedUtc { get; }
        public bool IsCancelled { get; private set; }

        public virtual void Dispose() {
            IsCancelled = true;
            _unsubscribeAction?.Invoke();
        }
    }
}
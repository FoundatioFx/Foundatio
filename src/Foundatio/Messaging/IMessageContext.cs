using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Messaging {
    public interface IMessageContext : IMessage, IDisposable {
        // message id
        string Id { get; }
        // message subscription id that received the message
        string SubscriptionId { get; }
        // when the message was originally published
        DateTime PublishedUtc { get; }
        // number of times this message has been delivered
        int DeliveryCount { get; }
        // acknowledge receipt of message and delete it
        Task AcknowledgeAsync();
        // reject the message as not having been successfully processed
        Task RejectAsync();
        // used to cancel processing of the current message
        CancellationToken CancellationToken { get; }
    }

    public interface IMessageContext<T> : IMessageContext, IMessage<T> where T: class {}

    public class MessageContext<T> : IMessageContext<T> where T : class {
        private readonly IMessageContext _context;

        public MessageContext(IMessageContext context) {
            _context = context;
        }

        public string Id => _context.Id;
        public string SubscriptionId => _context.SubscriptionId;
        public DateTime PublishedUtc => _context.PublishedUtc;
        public int DeliveryCount => _context.DeliveryCount;
        public CancellationToken CancellationToken => _context.CancellationToken;
        public string CorrelationId => _context.CorrelationId;
        public Type MessageType => _context.MessageType;
        public DateTime? ExpiresAtUtc => _context.ExpiresAtUtc;
        public DateTime? DeliverAtUtc => _context.DeliverAtUtc;
        public IReadOnlyDictionary<string, string> Properties => _context.Properties;
        public T Body => (T)GetBody();

        public object GetBody() {
            return _context.GetBody();
        }
        
        public Task AcknowledgeAsync() {
            return _context.AcknowledgeAsync();
        }

        public Task RejectAsync() {
            return _context.RejectAsync();
        }

        public void Dispose() {
            _context.Dispose();
        }
    }

    public class MessageContext : IMessageContext {
        protected readonly IMessage _message;
        protected readonly Func<Task> _acknowledgeAction;
        protected readonly Func<Task> _rejectAction;
        protected readonly Action _disposeAction;

        public MessageContext(string id, string subscriptionId, DateTime createdUtc, int deliveryCount,
            IMessage message, Func<Task> acknowledgeAction, Func<Task> rejectAction, Action disposeAction,
            CancellationToken cancellationToken = default) {
            Id = id;
            SubscriptionId = subscriptionId;
            PublishedUtc = createdUtc;
            DeliveryCount = deliveryCount;
            _message = message;
            _acknowledgeAction = acknowledgeAction;
            _rejectAction = rejectAction;
            _disposeAction = disposeAction;
            CancellationToken = cancellationToken;
        }

        public string Id { get; private set; }
        public string SubscriptionId { get; private set; }
        public DateTime PublishedUtc { get; private set; }
        public int DeliveryCount { get; private set; }
        public CancellationToken CancellationToken { get; private set; }
        public string CorrelationId => _message.CorrelationId;
        public Type MessageType => _message.MessageType;
        public DateTime? ExpiresAtUtc => _message.ExpiresAtUtc;
        public DateTime? DeliverAtUtc => _message.DeliverAtUtc;
        public IReadOnlyDictionary<string, string> Properties => _message.Properties;

        public object GetBody() {
            return _message.GetBody();
        }
        
        public Task AcknowledgeAsync() {
            if (_acknowledgeAction == null)
                return Task.CompletedTask;
            
            return _acknowledgeAction();
        }

        public Task RejectAsync() {
            if (_rejectAction == null)
                return Task.CompletedTask;
            
            return _rejectAction();
        }

        public void Dispose() {
            _disposeAction?.Invoke();
        }
    }
}
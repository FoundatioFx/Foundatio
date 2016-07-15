using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Utility;

namespace Foundatio.Messaging {
    public abstract class MessageBusBase : MaintenanceBase, IMessagePublisher {
        protected readonly ConcurrentDictionary<string, Subscriber> _subscribers = new ConcurrentDictionary<string, Subscriber>();
        private readonly ConcurrentDictionary<Guid, DelayedMessage> _delayedMessages = new ConcurrentDictionary<Guid, DelayedMessage>();
        
        public MessageBusBase(ILoggerFactory loggerFactory) : base(loggerFactory) {
            InitializeMaintenance();
        }

        public abstract Task PublishAsync(Type messageType, object message, TimeSpan? delay = null, CancellationToken cancellationToken = default(CancellationToken));

        protected async Task SendMessageToSubscribersAsync(Type messageType, object message) {
            if (message == null)
                return;

            var messageTypeSubscribers = _subscribers.Values.Where(s => s.Type.GetTypeInfo().IsAssignableFrom(messageType));
            foreach (var subscriber in messageTypeSubscribers) {
                if (subscriber.CancellationToken.IsCancellationRequested)
                    continue;

                try {
                    await subscriber.Action(message, subscriber.CancellationToken).AnyContext();
                } catch (Exception ex) {
                    _logger.Error(ex, "Error sending message to subscriber: {0}", ex.Message);
                }

                if (subscriber.CancellationToken.IsCancellationRequested) {
                    Subscriber sub;
                    if (_subscribers.TryRemove(subscriber.Id, out sub)) {
                        _logger.Trace("Removed cancelled subscriber: {subscriberId}", subscriber.Id);
                    } else {
                        _logger.Trace("Unable to remove cancelled subscriber: {subscriberId}", subscriber.Id);
                    }
                }
            }
        }

        public virtual void Subscribe<T>(Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken = default(CancellationToken)) where T : class {
            _logger.Trace("Adding subscriber for {0}.", typeof(T).FullName);
            var subscriber = new Subscriber {
                CancellationToken = cancellationToken,
                Type = typeof(T),
                Action = async (message, token) => {
                    if (!(message is T))
                        return;

                    await handler((T)message, cancellationToken).AnyContext();
                }
            };

            if (!_subscribers.TryAdd(subscriber.Id, subscriber))
                _logger.Error("Unable to add subscriber {subscriberId}", subscriber.Id);
        }
        
        protected Task AddDelayedMessageAsync(Type messageType, object message, TimeSpan delay) {
            if (message == null)
                throw new ArgumentNullException(nameof(message));
            
            var sendTime = SystemClock.UtcNow.Add(delay);
            _delayedMessages.TryAdd(Guid.NewGuid(), new DelayedMessage {
                Message = message,
                MessageType = messageType,
                SendTime = sendTime
            });

            ScheduleNextMaintenance(sendTime);
            return Task.CompletedTask;
        }
        
        protected override async Task<DateTime?> DoMaintenanceAsync() {
	        if (_delayedMessages == null || _delayedMessages.Count == 0)
                return DateTime.MaxValue;
            
            DateTime nextMessageSendTime = DateTime.MaxValue;
            var messagesToSend = new List<Guid>();

            // Add 50ms to the current time so we can batch up any other messages that will 
            // happen very shortly. Also the timer may run earilier than requested.
            var sendTime = SystemClock.UtcNow.AddMilliseconds(50);
            foreach (var pair in _delayedMessages) {
                if (pair.Value.SendTime <= sendTime)
                    messagesToSend.Add(pair.Key);
                else if (pair.Value.SendTime < nextMessageSendTime)
                    nextMessageSendTime = pair.Value.SendTime;
            }
            
            foreach (var messageId in messagesToSend) {
                DelayedMessage message;
                if (!_delayedMessages.TryRemove(messageId, out message))
                    continue;
                
                _logger.Trace("Sending delayed message scheduled for {0} for type {1}", message.SendTime.ToString("o"), message.MessageType);
                await PublishAsync(message.MessageType, message.Message).AnyContext();
            }

            _logger.Trace("DoMaintenance next message send time: {0}", nextMessageSendTime.ToString("o"));
            return nextMessageSendTime;
        }

        public override void Dispose() {
            base.Dispose();
            _delayedMessages?.Clear();
            _subscribers?.Clear();
        }

        protected class DelayedMessage {
            public DateTime SendTime { get; set; }
            public Type MessageType { get; set; }
            public object Message { get; set; }
        }
        
        protected class Subscriber {
            public string Id { get; private set; } = Guid.NewGuid().ToString();
            public CancellationToken CancellationToken { get; set; }
            public Type Type { get; set; }
            public Func<object, CancellationToken, Task> Action { get; set; }
        }
    }
}

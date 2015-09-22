using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Utility;

namespace Foundatio.Messaging {
    public abstract class MessageBusBase : IMessagePublisher, IDisposable {
        protected readonly ConcurrentDictionary<string, Subscriber> _subscribers = new ConcurrentDictionary<string, Subscriber>();

        private readonly ConcurrentDictionary<Guid, DelayedMessage> _delayedMessages = new ConcurrentDictionary<Guid, DelayedMessage>();
        private DateTime? _nextMaintenance = null;
        private readonly Timer _maintenanceTimer;

        public MessageBusBase() {
            _maintenanceTimer = new Timer(async s => await DoMaintenanceAsync(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public abstract Task PublishAsync(Type messageType, object message, TimeSpan? delay = null, CancellationToken cancellationToken = default(CancellationToken));

        protected async Task SendMessageToSubscribersAsync(Type messageType, object message) {
            var messageTypeSubscribers = _subscribers.Values.Where(s => s.Type.IsAssignableFrom(messageType));
            foreach (var subscriber in messageTypeSubscribers) {
                if (subscriber.CancellationToken.IsCancellationRequested)
                    continue;

                try {
                    await subscriber.Action(message, subscriber.CancellationToken);
                } catch (Exception ex) {
                    Logger.Error().Exception(ex).Message("Error sending message to subscriber: {0}", ex.Message).Write();
                }

                if (subscriber.CancellationToken.IsCancellationRequested) {
                    Subscriber sub;
                    if (_subscribers.TryRemove(subscriber.Id, out sub))
                        Logger.Trace().Message($"Removed cancelled subscriber: {subscriber.Id}").Write();
                    else
                        Logger.Trace().Message($"Unable to remove cancelled subscriber: {subscriber.Id}").Write();
                }
            }
        }

        public virtual void Subscribe<T>(Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken = default(CancellationToken)) where T : class {
            Logger.Trace().Message("Adding subscriber for {0}.", typeof(T).FullName).Write();
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
                Logger.Error().Message($"Unable to add subscriber {subscriber.Id}").Write();
        }
        
        protected Task AddDelayedMessageAsync(Type messageType, object message, TimeSpan delay) {
            if (message == null)
                throw new ArgumentNullException(nameof(message));
            
            var sendTime = DateTime.UtcNow.Add(delay);
            _delayedMessages.TryAdd(Guid.NewGuid(), new DelayedMessage {
                Message = message,
                MessageType = messageType,
                SendTime = sendTime
            });

            ScheduleNextMaintenance(sendTime);

            return TaskHelper.Completed();
        }

        private void ScheduleNextMaintenance(DateTime value) {
            Logger.Trace().Message("ScheduleNextMaintenance: value={0}", value).Write();
            if (value == DateTime.MaxValue)
                return;
            
            if (_nextMaintenance.HasValue && _nextMaintenance.Value < DateTime.UtcNow)
                _nextMaintenance = null;

            if (_nextMaintenance.HasValue && value > _nextMaintenance.Value)
                return;

            int delay = Math.Max((int)value.Subtract(DateTime.UtcNow).TotalMilliseconds, 0);
            _nextMaintenance = value;
            Logger.Trace().Message("Scheduling delayed task: delay={0}", delay).Write();
            _maintenanceTimer.Change(delay, Timeout.Infinite);
        }

        private async Task DoMaintenanceAsync() {
	        Logger.Trace().Message("DoMaintenanceAsync").Write();
            if (_delayedMessages == null || _delayedMessages.Count == 0)
                return;

            DateTime nextMessageSendTime = DateTime.MaxValue;
            var now = DateTime.UtcNow;
            var messagesToSend = new List<Guid>();

            foreach (var pair in _delayedMessages) {
                if (pair.Value.SendTime <= now)
                    messagesToSend.Add(pair.Key);
                else if (pair.Value.SendTime < nextMessageSendTime)
                    nextMessageSendTime = pair.Value.SendTime;
            }

            _nextMaintenance = null;
            ScheduleNextMaintenance(nextMessageSendTime);

            if (messagesToSend.Count == 0)
                return;

            foreach (var messageId in messagesToSend) {
                DelayedMessage message;
                if (!_delayedMessages.TryRemove(messageId, out message))
                    continue;

                Logger.Trace().Message("DoMaintenance Send Delayed: {0}", message.MessageType).Write();
                await PublishAsync(message.MessageType, message.Message).AnyContext();
            }
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

        public virtual void Dispose() {
            _maintenanceTimer.Dispose();
        }
    }
}

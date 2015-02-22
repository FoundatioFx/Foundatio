using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Messaging {
    public abstract class MessageBusBase : IMessagePublisher, IDisposable {
        private readonly CancellationTokenSource _queueDisposedCancellationTokenSource;
        private readonly List<DelayedMessage> _delayedMessages = new List<DelayedMessage>();

        public MessageBusBase() {
            _queueDisposedCancellationTokenSource = new CancellationTokenSource();
            TaskHelper.RunPeriodic(DoMaintenance, TimeSpan.FromMilliseconds(500), _queueDisposedCancellationTokenSource.Token, TimeSpan.FromMilliseconds(100));
        }

        private Task DoMaintenance() {
            Trace.WriteLine("Doing maintenance...");
            foreach (var message in _delayedMessages.Where(m => m.SendTime <= DateTime.Now).ToList()) {
                _delayedMessages.Remove(message);
                Publish(message.MessageType, message.Message);
            }

            return TaskHelper.Completed();
        }

        public abstract void Publish(Type messageType, object message, TimeSpan? delay = null);

        protected void AddDelayedMessage(Type messageType, object message, TimeSpan delay) {
            if (message == null)
                throw new ArgumentNullException("message");

            _delayedMessages.Add(new DelayedMessage { Message = message, MessageType = messageType, SendTime = DateTime.Now.Add(delay) });
        }

        protected class DelayedMessage {
            public DateTime SendTime { get; set; }
            public Type MessageType { get; set; }
            public object Message { get; set; }
        }

        public virtual void Dispose() {
            _queueDisposedCancellationTokenSource.Cancel();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Foundatio.Messaging {
    public abstract class MessageBusBase : IMessagePublisher, IDisposable {
        private readonly List<DelayedMessage> _delayedMessages = new List<DelayedMessage>();
        private readonly Timer _maintenanceTimer;

        public MessageBusBase() {
            _maintenanceTimer = new Timer(DoMaintenance, null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));
        }

        private readonly object _lock = new object();
        private void DoMaintenance(object state) {
            lock (_lock) {
                foreach (var message in _delayedMessages.Where(m => m.SendTime <= DateTime.Now).ToList()) {
                    _delayedMessages.Remove(message);
                    Publish(message.MessageType, message.Message);
                }
            }
        }

        public abstract void Publish(Type messageType, object message, TimeSpan? delay = null);

        protected void AddDelayedMessage(Type messageType, object message, TimeSpan delay) {
            if (message == null)
                throw new ArgumentNullException("message");

            lock (_lock)
                _delayedMessages.Add(new DelayedMessage { Message = message, MessageType = messageType, SendTime = DateTime.Now.Add(delay) });
        }

        protected class DelayedMessage {
            public DateTime SendTime { get; set; }
            public Type MessageType { get; set; }
            public object Message { get; set; }
        }

        public virtual void Dispose() {
            Trace.WriteLine("Disposing MessageBusBase");
            if (_maintenanceTimer != null)
                _maintenanceTimer.Dispose();
            Trace.WriteLine("Done Disposing MessageBusBase");
        }
    }
}

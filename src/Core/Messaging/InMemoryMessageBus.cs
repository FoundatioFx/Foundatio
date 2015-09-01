using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;

namespace Foundatio.Messaging {
    public class InMemoryMessageBus : MessageBusBase, IMessageBus {
        private readonly BlockingCollection<Subscriber> _subscribers = new BlockingCollection<Subscriber>();

        public override void Publish(Type messageType, object message, TimeSpan? delay = null) {
            if (delay.HasValue && delay.Value > TimeSpan.Zero) {
                AddDelayedMessage(messageType, message, delay.Value);
                return;
            }

            Task.Run(() => {
                foreach (var subscriber in _subscribers.Where(s => s.Type.IsAssignableFrom(messageType)).ToList()) {
                    try {
                        subscriber.Action(message.Copy());
                    } catch (Exception ex) {
                        Logger.Error().Exception(ex).Message("Error sending message to subscriber: {0}", ex.Message).Write();
                    }
                }
            });
        }

        public void Subscribe<T>(Action<T> handler) where T: class {
            _subscribers.Add(new Subscriber {
                Type = typeof(T),
                Action = m => {
                    if (!(m is T))
                        return;

                    handler(m as T);
                }
            });
        }

        private class Subscriber {
            public Type Type { get; set; }
            public Action<object> Action { get; set; }
        }
    }
}

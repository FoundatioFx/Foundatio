using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Utility;
using Nito.AsyncEx;

namespace Foundatio.Messaging {
    public class InMemoryMessageBus : MessageBusBase, IMessageBus {
        private readonly AsyncCollection<Subscriber> _subscribers = new AsyncCollection<Subscriber>();

        public override Task PublishAsync(Type messageType, object message, TimeSpan? delay = null, CancellationToken cancellationToken = default(CancellationToken)) {
            if (delay.HasValue && delay.Value > TimeSpan.Zero)
                return AddDelayedMessageAsync(messageType, message, delay.Value);

            Task.Run(() => {
                foreach (var subscriber in _subscribers.GetConsumingEnumerable().Where(s => s.Type.IsAssignableFrom(messageType)).ToList()) {
                    try {
                        subscriber.Action(message.Copy());
                    } catch (Exception ex) {
                        Logger.Error().Exception(ex).Message("Error sending message to subscriber: {0}", ex.Message).Write();
                    }
                }
            });

            return TaskHelper.Completed();
        }

        public void Subscribe<T>(Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken = default(CancellationToken)) where T : class {
            Logger.Trace().Message("Adding subscriber for {0}.", typeof(T).FullName).Write();
            _subscribers.Add(new Subscriber {
                Type = typeof(T),
                Action = async m => {
                    if (!(m is T))
                        return;

                    await handler((T)m, cancellationToken).AnyContext();
                }
            }, cancellationToken);
        }

        private class Subscriber {
            public Type Type { get; set; }
            public Action<object> Action { get; set; }
        }
    }
}

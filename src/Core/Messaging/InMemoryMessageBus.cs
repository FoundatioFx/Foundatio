using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Utility;
using Nito.AsyncEx;

namespace Foundatio.Messaging {
    public class InMemoryMessageBus : MessageBusBase, IMessageBus {
        private readonly BlockingCollection<Subscriber> _subscribers = new BlockingCollection<Subscriber>();

        public override Task PublishAsync(Type messageType, object message, TimeSpan? delay = null, CancellationToken cancellationToken = default(CancellationToken)) {
            if (delay.HasValue && delay.Value > TimeSpan.Zero)
                return AddDelayedMessageAsync(messageType, message, delay.Value);

            Task.Run(async () => {
                var messageTypeSubscribers = _subscribers.Where(s => s.Type.IsAssignableFrom(messageType)).ToList();
                Logger.Trace().Message("Found {0} of {1} subscribers for type: {2}", messageTypeSubscribers.Count, _subscribers.Count, messageType.FullName).Write();

                foreach (var subscriber in messageTypeSubscribers) {
                    try {
                        await subscriber.Action(message.Copy(), CancellationToken.None);
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
                Action = async (message, token) => {
                    if (!(message is T))
                        return;

                    await handler((T)message, cancellationToken).AnyContext();
                }
            }, cancellationToken);
        }

        private class Subscriber {
            public Type Type { get; set; }
            public Func<object, CancellationToken, Task> Action { get; set; }
        }
    }
}

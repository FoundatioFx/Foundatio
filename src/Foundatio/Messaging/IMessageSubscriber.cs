using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Messaging {
    public interface IMessageSubscriber {
        void Subscribe<T>(Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken = default(CancellationToken)) where T : class;
    }

    public static class MessageBusExtensions {
        public static void Subscribe<T>(this IMessageSubscriber subscriber, Func<T, Task> handler, CancellationToken cancellationToken = default (CancellationToken)) where T : class {
            subscriber.Subscribe<T>((msg, token) => handler(msg), cancellationToken);
        }

        public static void Subscribe<T>(this IMessageSubscriber subscriber, Action<T> handler, CancellationToken cancellationToken = default(CancellationToken)) where T : class {
            subscriber.Subscribe<T>((msg, token) => {
                handler(msg);
                return Task.CompletedTask;
            }, cancellationToken);
        }
    }
}

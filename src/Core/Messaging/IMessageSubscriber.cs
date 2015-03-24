using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Messaging {
    public interface IMessageSubscriber {
        void Subscribe<T>(Action<T> handler) where T : class;
    }

    public interface IMessageSubscriber2 {
        void Subscribe<T>(Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken = default(CancellationToken)) where T : class;
    }

    public static class MessageBusExtensions {
        public static void Subscribe<T>(this IMessageSubscriber2 subscriber, Func<T, Task> handler, CancellationToken cancellationToken = default (CancellationToken)) where T : class {
            subscriber.Subscribe<T>((msg, token) => handler(msg), cancellationToken);
        }

        public static void Subscribe<T>(this IMessageSubscriber2 subscriber, Action<T> handler, CancellationToken cancellationToken = default (CancellationToken)) where T : class {
            subscriber.Subscribe<T>((msg, token) => handler(msg), cancellationToken);
        }
    }
}

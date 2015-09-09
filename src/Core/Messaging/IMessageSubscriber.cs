using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Messaging {
    public interface IMessageSubscriber {
        Task SubscribeAsync<T>(Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken = default(CancellationToken)) where T : class;
    }

    public static class MessageBusExtensions {
        public static Task SubscribeAsync<T>(this IMessageSubscriber subscriber, Func<T, Task> handler, CancellationToken cancellationToken = default (CancellationToken)) where T : class {
            return subscriber.SubscribeAsync<T>((msg, token) => handler(msg), cancellationToken);
        }

        public static Task SubscribeAsync<T>(this IMessageSubscriber subscriber, Action<T> handler, CancellationToken cancellationToken = default (CancellationToken)) where T : class {
            return subscriber.SubscribeAsync<T>((msg, token) => {
                handler(msg);
                return TaskHelper.Completed();
            }, cancellationToken);
        }
    }
}

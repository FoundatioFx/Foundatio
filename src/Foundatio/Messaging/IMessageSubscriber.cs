using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Messaging {
    public interface IHandle<T> where T: class {
        Task Handle(IMessageContext<T> context);
    }

    public interface IMessageSubscriber : IDisposable {
        Task<IMessageSubscription> SubscribeAsync(MessageSubscriptionOptions options, Func<IMessageContext, Task> handler);
        Task<IMessageContext> ReceiveAsync(MessageReceiveOptions options);
    }

    public class MessageSubscriptionOptions {
        public Type MessageType { get; set; }
        public int PrefetchCount { get; set; } = 1;
        public CancellationToken CancellationToken { get; set; }

        public MessageSubscriptionOptions WithMessageType(Type messageType) {
            MessageType = messageType;
            return this;
        }

        public MessageSubscriptionOptions WithPrefetchCount(int prefetchCount) {
            PrefetchCount = prefetchCount;
            return this;
        }

        public MessageSubscriptionOptions WithCancellationToken(CancellationToken cancellationToken) {
            CancellationToken = cancellationToken;
            return this;
        }
    }

    public class MessageReceiveOptions {
        public Type MessageType { get; set; }
        public TimeSpan Timeout { get; set; }
        public CancellationToken CancellationToken { get; set; }

        public MessageReceiveOptions WithMessageType(Type messageType) {
            MessageType = messageType;
            return this;
        }

        public MessageReceiveOptions WithTimeout(TimeSpan timeout) {
            Timeout = timeout;
            return this;
        }

        public MessageReceiveOptions WithCancellationToken(CancellationToken cancellationToken) {
            CancellationToken = cancellationToken;
            return this;
        }
    }

    public static class MessageBusExtensions {
        public static async Task<IMessageSubscription> SubscribeAsync<T>(this IMessageSubscriber subscriber, Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken = default) where T : class {
            if (cancellationToken.IsCancellationRequested)
                return new MessageSubscription(typeof(T), () => {});
            
            var options = new MessageSubscriptionOptions().WithMessageType(typeof(T)).WithCancellationToken(cancellationToken);
            var subscription = await subscriber.SubscribeAsync(options, (msg) => handler((T)msg.GetBody(), msg.CancellationToken)).AnyContext();
            if (cancellationToken != CancellationToken.None)
                cancellationToken.Register(() => subscription.Dispose());
            
            return subscription;
        }

        public static Task<IMessageSubscription> SubscribeAsync<T>(this IMessageSubscriber subscriber, Func<T, Task> handler, CancellationToken cancellationToken = default) where T : class {
            return subscriber.SubscribeAsync<T>((msg, token) => handler(msg), cancellationToken);
        }

        public static Task<IMessageSubscription> SubscribeAsync<T>(this IMessageSubscriber subscriber, Action<T> handler, CancellationToken cancellationToken = default) where T : class {
            return subscriber.SubscribeAsync<T>((msg, token) => {
                handler(msg);
                return Task.CompletedTask;
            }, cancellationToken);
        }

        public static Task SubscribeAsync(this IMessageSubscriber subscriber, Func<IMessage, CancellationToken, Task> handler, CancellationToken cancellationToken = default) {
            return subscriber.SubscribeAsync<IMessage>((msg, token) => handler(msg, token), cancellationToken);
        }

        public static Task SubscribeAsync(this IMessageSubscriber subscriber, Func<IMessage, Task> handler, CancellationToken cancellationToken = default) {
            return subscriber.SubscribeAsync((msg, token) => handler(msg), cancellationToken);
        }

        public static Task SubscribeAsync(this IMessageSubscriber subscriber, Action<IMessage> handler, CancellationToken cancellationToken = default) {
            return subscriber.SubscribeAsync((msg, token) => {
                handler(msg);
                return Task.CompletedTask;
            }, cancellationToken);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Messaging {
    public interface IMessageSubscriber {
        Task<IMessageSubscription> SubscribeAsync<T>(Func<T, CancellationToken, Task> handler, MessageSubscriptionOptions options = null) where T : class;
    }

    public static class MessageBusExtensions {
        public static Task<IMessageSubscription> SubscribeAsync<T>(this IMessageSubscriber subscriber, Func<T, Task> handler, MessageSubscriptionOptions options = null) where T : class {
            return subscriber.SubscribeAsync<T>((msg, token) => handler(msg), options);
        }

        public static Task<IMessageSubscription> SubscribeAsync<T>(this IMessageSubscriber subscriber, Action<T> handler, MessageSubscriptionOptions options = null) where T : class {
            return subscriber.SubscribeAsync<T>((msg, token) => {
                handler(msg);
                return Task.CompletedTask;
            }, options);
        }

        public static Task<IMessageSubscription> SubscribeAsync(this IMessageSubscriber subscriber, Func<IMessage, CancellationToken, Task> handler, MessageSubscriptionOptions options = null) {
            return subscriber.SubscribeAsync<IMessage>((msg, token) => handler(msg, token), options);
        }

        public static Task<IMessageSubscription> SubscribeAsync(this IMessageSubscriber subscriber, Func<IMessage, Task> handler, MessageSubscriptionOptions options = null) {
            return subscriber.SubscribeAsync((msg, token) => handler(msg), options);
        }

        public static Task<IMessageSubscription> SubscribeAsync(this IMessageSubscriber subscriber, Action<IMessage> handler, MessageSubscriptionOptions options = null) {
            return subscriber.SubscribeAsync((msg, token) => {
                handler(msg);
                return Task.CompletedTask;
            }, options);
        }
    }

    public interface IMessageSubscription : IAsyncDisposable {
        string SubscriptionId { get; }
        IDictionary<string, string> Properties { get; }
    }

    public class MessageSubscription : IMessageSubscription {
        private readonly Func<ValueTask> _disposeSubscriptionFunc;

        public MessageSubscription(string subscriptionId, Func<ValueTask> disposeSubscriptionFunc) {
            SubscriptionId = subscriptionId;
            _disposeSubscriptionFunc = disposeSubscriptionFunc;
        }

        public string SubscriptionId { get; }
        public IDictionary<string, string> Properties { get; set; } = new Dictionary<string, string>();

        public ValueTask DisposeAsync() {
            return _disposeSubscriptionFunc?.Invoke() ?? new ValueTask();
        }
    }

    public class MessageSubscriptionOptions {
        /// <summary>
        /// The topic name
        /// </summary>
        public string Topic { get; set; }

        /// <summary>
        /// Resolves a message to a .NET type.
        /// </summary>
        public Func<IConsumeMessageContext, Type> MessageTypeResolver { get; set; }

        public CancellationToken CancellationToken { get; set; }

        public IDictionary<string, string> Properties { get; set; } = new Dictionary<string, string>();

        public static implicit operator MessageSubscriptionOptions(CancellationToken cancellationToken) => new() {  CancellationToken = cancellationToken };
    }
}

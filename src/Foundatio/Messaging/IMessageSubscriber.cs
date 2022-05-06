﻿using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Messaging {
    public interface IMessageSubscriber {
        // there will be extensions that allow subscribing via generic message type parameters with and without the message context wrapper
        Task<IMessageSubscription> SubscribeAsync(Func<IMessageContext, Task> handler, IMessageSubscriptionOptions options);
    }
    
    public interface IMessageSubscriptionOptions {
        // message type for the subscription
        Type MessageType { get; }
    }

    public interface IMessageSubscription : IDisposable {
        // subscription id
        string Id { get; }
        // name of the queue that this subscription is listening to
        string QueueName { get; }
        // when was the message created
        DateTime CreatedUtc { get; }
    }

    public interface IMessageContext : IMessage, IMessagePublisher, IDisposable {
        // message id
        string Id { get; }
        // when the message was originally created
        DateTime CreatedUtc { get; }
        // number of times this message has been delivered
        int DeliveryCount { get; }
        // acknowledge receipt of message and delete it
        Task AcknowledgeAsync();
        // reject the message as not having been successfully processed
        Task RejectAsync();
        CancellationToken CancellationToken { get; }
    }

    public interface IMessageContext<T> : IMessageContext, IMessage<T> where T: class {}

    public static class MessageBusExtensions {
        public static async Task SubscribeAsync<T>(this IMessageSubscriber subscriber, Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken = default) where T : class {
            if (cancellationToken.IsCancellationRequested)
                return;
            
            var result = await subscriber.SubscribeAsync<T>((msg, token) => handler(msg, token));
            if (cancellationToken != CancellationToken.None)
                cancellationToken.Register(() => ThreadPool.QueueUserWorkItem(s => result?.Dispose()));
        }

        public static Task SubscribeAsync<T>(this IMessageSubscriber subscriber, Func<T, Task> handler, CancellationToken cancellationToken = default) where T : class {
            return subscriber.SubscribeAsync<T>((msg, token) => handler(msg), cancellationToken);
        }

        public static Task SubscribeAsync<T>(this IMessageSubscriber subscriber, Action<T> handler, CancellationToken cancellationToken = default) where T : class {
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

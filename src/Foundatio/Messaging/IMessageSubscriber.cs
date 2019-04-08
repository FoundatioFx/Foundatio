using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Messaging {
    // save our subscription handlers in memory so that they can be restored if the connection is interupted

    // should we have a transport interface that we implement and then have more concrete things in the public
    // interface classes?
    public interface IMessageTransport {
        Task<IMessageSubscription> SubscribeAsync(Func<IMessageContext, Task> handler, IMessageSubscriptionOptions options);
        Task PublishAsync(IMessage message);
    }

    public interface IMessageSubscriber {
        // there will be extensions that allow subscribing via generic message type parameters with and without the message context wrapper
        Task<IMessageSubscription> SubscribeAsync(Func<IMessageContext, Task> handler, IMessageSubscriptionOptions options);
        
        // the methods below will be extension methods that call the method above
        Task<IMessageSubscription> SubscribeAsync<T>(Func<T, Task> handler) where T: class;
        Task<IMessageSubscription> SubscribeAsync<T>(Func<IMessageContext<T>, Task> handler) where T: class;
        Task<IMessageSubscription> SubscribeAsync<T>(Action<T> handler) where T: class;
        Task<IMessageSubscription> SubscribeAsync<T>(Action<IMessageContext<T>> handler) where T: class;
    }
    
    public interface IMessageSubscriptionOptions {
        // message type for the subscription
        Type MessageType { get; }
    }

    public interface IMessageSubscription : IDisposable {
        // subscription id
        string Id { get; }
        // name of the queue that this subscription is listening to
        Type MessageType { get; }
        // when was the message created
        DateTime CreatedUtc { get; }
    }

    public interface IMessageContext : IMessage, IMessagePublisher, IDisposable {
        // message id
        string Id { get; }
        // message subscription id that received the message
        string SubscriptionId { get; }
        // when the message was originally created
        DateTime CreatedUtc { get; }
        // number of times this message has been delivered
        int DeliveryCount { get; }
        // acknowledge receipt of message and delete it
        Task AcknowledgeAsync();
        // reject the message as not having been successfully processed
        Task RejectAsync();
        // used to reply to messages that have a replyto specified
        Task ReplyAsync<T>(T message) where T: class;
        // used to cancel processing of the current message
        CancellationToken CancellationToken { get; }
    }

    public interface IWorkScheduler {
        // ability to persist work items and schedule them for execution at a later time
        // not sure if it should be specific to messaging or just generically useful
        // Should grab items and work very similar to queue (ability to batch dequeue)
        // worker probably in different interface so processing can be separate from scheduling.
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
    }
}

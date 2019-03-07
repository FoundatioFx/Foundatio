using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Messaging {
    // conventions to determine message queue based on message type
    // conventions for determining default time to live, retries, and deadletter behavior based on message type
    // pub/sub you would publish messages and each subscriber would automatically get a unique subscriber id and the messages would go to all of them
    // worker you would publish messages and each subscriber would use the same subscriber id and the messages would get round robin'd
    // need to figure out how we would handle rpc request / response. need to be able to subscribe for a single message
    public interface IMessageSubscriber {
        // there will be extensions that allow subscribing via generic message type parameters with and without the message context wrapper
        Task<IMessageSubscription> SubscribeAsync(Func<IMessageContext, Task> handler, IMessageSubscriptionOptions options);
    }
    
    public interface IMessageSubscriptionOptions {
        // message type for the subscription
        Type MessageType { get; }
        // topic to use for subscription, if left blank it will be calculated using conventions from the message type
        string Topic { get; set; }
        // subscription id, for worker queues use the same subscription id for all subscriptions and the messages will be round robin'd
        string SubscriptionId { get; set; }
        // how many messages should be fetched at a time
        int PrefetchSize { get; set; }
        // what priority level messages should this subscription receive
        int Priority { get; set; }
        // how long should the message remain in flight before timing out
        TimeSpan TimeToLive { get; set; }
        // how messages should be acknowledged
        AcknowledgementStrategy AcknowledgementStrategy { get; set; }
    }

    public enum AcknowledgementStrategy {
        Manual, // consumer needs to do it
        Automatic, // auto acknowledge after handler completes successfully and auto reject if handler throws
        FireAndForget // acknowledge before handler runs
    }

    public interface IMessageSubscription : IDisposable {
        // the subscription id
        string Id { get; }
        // topic that this subscription is listening to
        string Topic { get; }
        // when was the message created
        DateTime CreatedUtc { get; }
    }

    public interface IMessage<T> : IMessage where T: class {
        T Body { get; }
    }

    public interface IMessage {
        // correlation id used in logging
        string CorrelationId { get; }
        // used for rpc (request/reply)
        string ReplyTo { get; }
        // message priority
        int Priority { get; }
        // topic the message will be sent to
        string Topic { get; }
        // message type, will be converted to string and stored with the message for deserialization
        Type MessageType { get; }
        // message body
        object GetBody();
        // when the message should expire
        DateTime ExpiresAtUtc { get; }
        // additional data to store with the message
        DataDictionary Data { get; }
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
    }
}

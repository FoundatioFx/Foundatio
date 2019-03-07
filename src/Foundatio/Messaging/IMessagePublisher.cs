using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Messaging {
    public interface IMessagePublisher {
        // extensions for easily publishing just the raw message body, message settings populated from conventions
        Task PublishAsync(IMessage message);
    }

    public interface IMessage<T> : IMessage where T: class {
        T Body { get; }
    }

    public interface IMessage {
        // correlation id used in logging
        string CorrelationId { get; }
        // used for rpc (request/reply)
        string ReplyTo { get; }
        // message type, will be converted to string and stored with the message for deserialization
        Type MessageType { get; }
        // message body
        object GetBody();
        // when the message should expire
        DateTime? ExpiresAtUtc { get; }
        // additional data to store with the message
        DataDictionary Data { get; }
    }
    
    public delegate IMessageQueueOptions GetMessageQueueOptions(Type messageType);

    public interface IMessageQueueOptions {
        // whether messages will survive transport restart
        bool IsDurable { get; set; }
        // if worker, subscriptions will default to using the same subscription id and 
        bool IsWorker { get; set; }
        // the name of the queue that the messages are stored in
        string QueueName { get; set; }
        // how long should the message remain in flight before timing out
        TimeSpan TimeToLive { get; set; }
        // how many messages should be fetched at a time
        int PrefetchSize { get; set; }
        // how messages should be acknowledged
        AcknowledgementStrategy AcknowledgementStrategy { get; set; }
        // need something for how to handle retries and deadletter
    }

    public enum AcknowledgementStrategy {
        Manual, // consumer needs to do it
        Automatic, // auto acknowledge after handler completes successfully and auto reject if handler throws
        FireAndForget // acknowledge before handler runs
    }

    public static class MessagePublisherExtensions {
        public static Task PublishAsync<T>(this IMessagePublisher publisher, T message) {
            var m = new Message<T>();
            return publisher.PublishAsync(m);
        }

        public static Task PublishAsync<T>(this IMessagePublisher publisher, T message, TimeSpan? delay = null) where T : class {
            return publisher.PublishAsync(typeof(T), message, delay);
        }
    }
}

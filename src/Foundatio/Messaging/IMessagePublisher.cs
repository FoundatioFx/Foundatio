using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Messaging {
    public interface IMessagePublisher {
        // extensions for easily publishing just the raw message body, message settings populated from conventions
        Task PublishAsync(IMessage message);
        
        // the methods below will be extension methods that call the method above
        Task PublishAsync<T>(T message) where T: class;
    }

    public interface IMessage<T> : IMessage where T: class {
        T Body { get; }
    }

    public interface IMessageSerializer {
        // used to serialize and deserialize messages. Normally just a wrapper for ISerializer, but
        // can be used to transform messages to/from different formats from different systems
        // needs ability to read and populate messages headers
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
        // when the message should be delivered when using delayed delivery
        DateTime? DeliverAtUtc { get; }
        // additional message data to store with the message
        DataDictionary Headers { get; }
    }

    public static class MessagePublisherExtensions {
        public static Task PublishAsync<T>(this IMessagePublisher publisher, T message, TimeSpan? delay = null) where T : class {
            return publisher.PublishAsync(typeof(T), message, delay);
        }
    }
}

// unified messaging
//  lots of metrics / stats on messaging
// unify job / startup actions
// add tags, key/value pairs to metrics
// auto renewing locks
// get rid of queues / queue job
// simplify the way jobs are run


using System;

namespace Foundatio.Messaging {
    // work items go away, queues go away. everything consolidated down to just messaging
    // will be easy to handle random messages like you can with work items currently
    // conventions to determine message queue based on message type
    // conventions for determining default time to live, retries, and deadletter behavior based on message type, whether its a worker type or not
    // pub/sub you would publish messages and each subscriber would automatically get a unique subscriber id and the messages would go to all of them
    // worker you would publish messages and each subscriber would use the same subscriber id and the messages would get round robin'd
    // need to figure out how we would handle rpc request / response. need to be able to subscribe for a single message
    // still need interceptors / middleware to replace queue behaviors

    public interface IMessageBus : IMessagePublisher, IMessageSubscriber, IDisposable {}
    
    public delegate IMessageQueueOptions GetMessageQueueOptions(Type messageType);

    // this is used to get the message type from the string type that is stored in the message properties
    // this by default would be the message types full name, but it could be something completely different
    // especially if a message is being read that was published by some other non-dotnet library
    public interface IMessageTypeConverter {
        string ToString(Type messageType);
        Type FromString(string messageType);
    }

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
}
using System.Linq;
using Amazon.SQS.Model;

namespace Foundatio.Queues;

public class SQSQueueEntry<T>
    : QueueEntry<T> where T : class
{
    public Message UnderlyingMessage { get; }

    public SQSQueueEntry(Message message, T value, IQueue<T> queue)
        : base(message.MessageId, message.CorrelationId(), value, queue, message.SentTimestamp(), message.ApproximateReceiveCount())
    {
        if (message.MessageAttributes is not null)
        {
            foreach (var property in message.MessageAttributes.Where(a => a.Key != "CorrelationId"))
                Properties.Add(property.Key, property.Value.StringValue);
        }

        UnderlyingMessage = message;
    }
}

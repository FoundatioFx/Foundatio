using System;
using Amazon.SQS.Model;
using Foundatio.Queues;

namespace Foundatio.AWS.Queues
{
    internal class SQSQueueEntry<T>
        : QueueEntry<T> where T : class {
        public Message UnderlyingMessage { get; }

        internal SQSQueueEntry(Message message, T value, IQueue<T> queue)
            : base(message.MessageId, value, queue, DateTime.MinValue, 0) {

            UnderlyingMessage = message;
        }
    }
}
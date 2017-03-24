using System;
using Amazon.SQS.Model;

namespace Foundatio.Queues {
    internal class SQSQueueEntry<T>
        : QueueEntry<T> where T : class {

        public Message UnderlyingMessage { get; }

        internal SQSQueueEntry(Message message, T value, IQueue<T> queue)
            : base(message.MessageId, value, queue, message.SentTimestamp(), message.ApproximateReceiveCount()) {

            UnderlyingMessage = message;
        }
    }
}
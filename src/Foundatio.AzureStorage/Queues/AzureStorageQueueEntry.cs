using System;
using Microsoft.WindowsAzure.Storage.Queue;

namespace Foundatio.Queues {
    public class AzureStorageQueueEntry<T> : QueueEntry<T> where T : class {
        public CloudQueueMessage UnderlyingMessage { get; }

        public AzureStorageQueueEntry(CloudQueueMessage message, T value, IQueue<T> queue)
            : base(message.Id, null, value, queue, message.InsertionTime.GetValueOrDefault().UtcDateTime, message.DequeueCount) {

            UnderlyingMessage = message;
        }
    }
}
using Foundatio.Queues;
using Microsoft.WindowsAzure.Storage.Queue;

namespace Foundatio.AzureStorage.Queues {
    internal class AzureStorageQueueEntry<T> : QueueEntry<T> where T : class {
        internal CloudQueueMessage UnderlyingMessage { get; }

        internal AzureStorageQueueEntry(CloudQueueMessage message, T value, IQueue<T> queue)
            : base(message.Id, value, queue, message.InsertionTime.GetValueOrDefault().UtcDateTime, message.DequeueCount) {

            UnderlyingMessage = message;
        }
    }
}
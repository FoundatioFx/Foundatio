using System;
using System.Collections.Generic;
using Azure.Storage.Queues.Models;

namespace Foundatio.Queues;

public class AzureStorageQueueEntry<T> : QueueEntry<T> where T : class
{
    public QueueMessage UnderlyingMessage { get; }

    /// <summary>
    /// The current pop receipt for this message. This gets updated after each
    /// UpdateMessageAsync call (renew lock, abandon with retry delay).
    /// </summary>
    public string PopReceipt { get; internal set; }

    public AzureStorageQueueEntry(QueueMessage message, string correlationId, IDictionary<string, string> properties, T data, IQueue<T> queue)
        : base(message.MessageId, correlationId, data, queue, message.InsertedOn?.UtcDateTime ?? DateTime.MinValue, (int)message.DequeueCount)
    {
        UnderlyingMessage = message;
        PopReceipt = message.PopReceipt;

        if (properties != null)
        {
            foreach (var property in properties)
                Properties[property.Key] = property.Value;
        }
    }
}

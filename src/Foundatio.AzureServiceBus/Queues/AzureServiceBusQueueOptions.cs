using System;
using Microsoft.ServiceBus;
using Microsoft.ServiceBus.Messaging;

namespace Foundatio.Queues {
    public class AzureServiceBusQueueOptions<T> : QueueOptionsBase<T> where T : class {
        public string ConnectionString { get; set; }

        /// <summary>
        /// The queue idle interval after which the queue is automatically deleted.
        /// </summary>
        public TimeSpan? AutoDeleteOnIdle { get; set; }

        /// <summary>
        /// The maximum size of the queue in megabytes.
        /// </summary>
        public long? MaxSizeInMegabytes { get; set; }

        /// <summary>
        /// Set to true if queue requires duplicate detection.
        /// </summary>
        public bool? RequiresDuplicateDetection { get; set; }

        /// <summary>
        /// Set to true if queue supports the concept of sessions.
        /// </summary>
        public bool? RequiresSession { get; set; }

        /// <summary>
        /// The default message time to live.
        /// </summary>
        public TimeSpan? DefaultMessageTimeToLive { get; set; }

        /// <summary>
        /// Returns true if the queue has dead letter support when a message expires.
        /// </summary>
        public bool? EnableDeadLetteringOnMessageExpiration { get; set; }

        /// <summary>
        /// The duration of the duplicate detection history.
        /// </summary>
        public TimeSpan? DuplicateDetectionHistoryTimeWindow { get; set; }

        /// <summary>
        /// Returns true if server-side batched operations are enabled.
        /// </summary>
        public bool? EnableBatchedOperations { get; set; }

        /// <summary>
        /// Returns true if the message is anonymous accessible.
        /// </summary>
        public bool? IsAnonymousAccessible { get; set; }

        /// <summary>
        /// Returns true if the queue supports ordering.
        /// </summary>
        public bool? SupportOrdering { get; set; }

        /// <summary>
        /// Returns the status of the queue (enabled or disabled). When an entity is disabled, that entity cannot send or receive messages.
        /// </summary>
        public EntityStatus? Status { get; set; }

        /// <summary>
        /// Returns the path to the recipient to which the message is forwarded.
        /// </summary>
        public string ForwardTo { get; set; }

        /// <summary>
        /// Returns the path to the recipient to which the dead lettered message is forwarded.
        /// </summary>
        public string ForwardDeadLetteredMessagesTo { get; set; }

        /// <summary>
        /// Returns true if the queue is to be partitioned across multiple message brokers.
        /// </summary>
        public bool? EnablePartitioning { get; set; }

        /// <summary>
        /// Returns user metadata.
        /// </summary>
        public string UserMetadata { get; set; }

        /// <summary>
        /// Returns true if the queue holds a message in memory temporarily before writing it to persistent storage.
        /// </summary>
        public bool? EnableExpress { get; set; }

        /// <summary>
        /// Returns the retry policy;
        /// </summary>
        public RetryPolicy RetryPolicy { get; set; }
    }
}
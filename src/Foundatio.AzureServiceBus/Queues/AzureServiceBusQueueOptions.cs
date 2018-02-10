using System;
using Microsoft.ServiceBus;
using Microsoft.ServiceBus.Messaging;

namespace Foundatio.Queues {
    public class AzureServiceBusQueueOptions<T> : SharedQueueOptions<T> where T : class {
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

    public class AzureServiceBusQueueOptionsBuilder<T> : SharedQueueOptionsBuilder<T, AzureServiceBusQueueOptions<T>, AzureServiceBusQueueOptionsBuilder<T>> where T : class {
        public AzureServiceBusQueueOptionsBuilder<T> ConnectionString(string connectionString) {
            Target.ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            return this;
        }

        public AzureServiceBusQueueOptionsBuilder<T> AutoDeleteOnIdle(TimeSpan autoDeleteOnIdle) {
            Target.AutoDeleteOnIdle = autoDeleteOnIdle;
            return this;
        }

        public AzureServiceBusQueueOptionsBuilder<T> MaxSizeInMegabytes(long maxSizeInMegabytes) {
            Target.MaxSizeInMegabytes = maxSizeInMegabytes;
            return this;
        }

        public AzureServiceBusQueueOptionsBuilder<T> RequiresDuplicateDetection(bool requiresDuplicateDetection) {
            Target.RequiresDuplicateDetection = requiresDuplicateDetection;
            return this;
        }

        public AzureServiceBusQueueOptionsBuilder<T> RequiresSession(bool requiresSession) {
            Target.RequiresSession = requiresSession;
            return this;
        }

        public AzureServiceBusQueueOptionsBuilder<T> DefaultMessageTimeToLive(TimeSpan defaultMessageTimeToLive) {
            Target.DefaultMessageTimeToLive = defaultMessageTimeToLive;
            return this;
        }

        public AzureServiceBusQueueOptionsBuilder<T> EnableDeadLetteringOnMessageExpiration(bool enableDeadLetteringOnMessageExpiration) {
            Target.EnableDeadLetteringOnMessageExpiration = enableDeadLetteringOnMessageExpiration;
            return this;
        }

        public AzureServiceBusQueueOptionsBuilder<T> DuplicateDetectionHistoryTimeWindow(TimeSpan duplicateDetectionHistoryTimeWindow) {
            Target.DuplicateDetectionHistoryTimeWindow = duplicateDetectionHistoryTimeWindow;
            return this;
        }

        public AzureServiceBusQueueOptionsBuilder<T> EnableBatchedOperations(bool enableBatchedOperations) {
            Target.EnableBatchedOperations = enableBatchedOperations;
            return this;
        }

        public AzureServiceBusQueueOptionsBuilder<T> IsAnonymousAccessible(bool isAnonymousAccessible) {
            Target.IsAnonymousAccessible = isAnonymousAccessible;
            return this;
        }

        public AzureServiceBusQueueOptionsBuilder<T> SupportOrdering(bool supportOrdering) {
            Target.SupportOrdering = supportOrdering;
            return this;
        }

        public AzureServiceBusQueueOptionsBuilder<T> Status(EntityStatus status) {
            Target.Status = status;
            return this;
        }

        public AzureServiceBusQueueOptionsBuilder<T> ForwardTo(string forwardTo) {
            Target.ForwardTo = forwardTo ?? throw new ArgumentNullException(nameof(forwardTo));
            return this;
        }

        public AzureServiceBusQueueOptionsBuilder<T> ForwardDeadLetteredMessagesTo(string forwardDeadLetteredMessagesTo) {
            Target.ForwardDeadLetteredMessagesTo = forwardDeadLetteredMessagesTo ?? throw new ArgumentNullException(nameof(forwardDeadLetteredMessagesTo));
            return this;
        }

        public AzureServiceBusQueueOptionsBuilder<T> EnablePartitioning(bool enablePartitioning) {
            Target.EnablePartitioning = enablePartitioning;
            return this;
        }

        public AzureServiceBusQueueOptionsBuilder<T> UserMetadata(string userMetadata) {
            Target.UserMetadata = userMetadata ?? throw new ArgumentNullException(nameof(userMetadata));
            return this;
        }

        public AzureServiceBusQueueOptionsBuilder<T> EnableExpress(bool enableExpress) {
            Target.EnableExpress = enableExpress;
            return this;
        }

        public AzureServiceBusQueueOptionsBuilder<T> RetryPolicy(RetryPolicy retryPolicy) {
            Target.RetryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
            return this;
        }
    }
}
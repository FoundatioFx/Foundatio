using System;
using Microsoft.ServiceBus;
using Microsoft.ServiceBus.Messaging;

namespace Foundatio.Messaging {
    public class AzureServiceBusMessageBusOptions : MesssageBusOptions {
        public string ConnectionString { get; set; }

        /// <summary>
        /// Prefetching enables the queue or subscription client to load additional messages from the service when it performs a receive operation.
        /// https://docs.microsoft.com/en-us/azure/service-bus-messaging/service-bus-performance-improvements
        /// </summary>
        public int? PrefetchCount { get; set; }

        /// <summary>
        /// The idle interval after which the topic is automatically deleted. The minimum duration is 5 minutes.
        /// </summary>
        public TimeSpan? TopicAutoDeleteOnIdle { get; set; }

        /// <summary>
        /// The default message time to live value for a topic
        /// </summary>
        public TimeSpan? TopicDefaultMessageTimeToLive { get; set; }

        /// <summary>
        /// The maximum size of the topic in megabytes.
        /// </summary>
        public long? TopicMaxSizeInMegabytes { get; set; }

        /// <summary>
        /// Set to true if topic requires duplicate detection.
        /// </summary>
        public bool? TopicRequiresDuplicateDetection { get; set; }

        /// <summary>
        /// The duration of the duplicate detection history.
        /// </summary>
        public TimeSpan? TopicDuplicateDetectionHistoryTimeWindow { get; set; }

        /// <summary>
        /// Returns true if server-side batched operations are enabled.
        /// </summary>
        public bool? TopicEnableBatchedOperations { get; set; }

        /// <summary>
        /// Controls whether messages should be filtered before publishing.
        /// </summary>
        public bool? TopicEnableFilteringMessagesBeforePublishing { get; set; }

        /// <summary>
        /// Returns true if the message is anonymous accessible.
        /// </summary>
        public bool? TopicIsAnonymousAccessible { get; set; }

        /// <summary>
        /// Returns the status of the topic (enabled or disabled). When an entity is disabled, that entity cannot send or receive messages.
        /// </summary>
        public EntityStatus? TopicStatus { get; set; }

        /// <summary>
        /// Returns true if the queue supports ordering.
        /// </summary>
        public bool? TopicSupportOrdering { get; set; }

        /// <summary>
        /// Returns true if the topic is to be partitioned across multiple message brokers.
        /// </summary>
        public bool? TopicEnablePartitioning { get; set; }

        /// <summary>
        /// Returns true if the queue holds a message in memory temporarily before writing it to persistent storage.
        /// </summary>
        public bool? TopicEnableExpress { get; set; }

        /// <summary>
        /// Returns user metadata.
        /// </summary>
        public string TopicUserMetadata { get; set; }

        /// <summary>
        /// If no subscription name is specified, then a fanout type message bus will be created.
        /// </summary>
        public string SubscriptionName { get; set; }

        /// <summary>
        /// The idle interval after which the subscription is automatically deleted. The minimum duration is 5 minutes.
        /// </summary>
        public TimeSpan? SubscriptionAutoDeleteOnIdle { get; set; }

        /// <summary>
        /// The default message time to live.
        /// </summary>
        public TimeSpan? SubscriptionDefaultMessageTimeToLive { get; set; }

        /// <summary>
        /// The lock duration time span for the subscription.
        /// </summary>
        public TimeSpan? SubscriptionWorkItemTimeout { get; set; }

        /// <summary>
        /// the value indicating if a subscription supports the concept of session.
        /// </summary>
        public bool? SubscriptionRequiresSession { get; set; }

        /// <summary>
        /// Returns true if the subscription has dead letter support when a message expires.
        /// </summary>
        public bool? SubscriptionEnableDeadLetteringOnMessageExpiration { get; set; }

        /// <summary>
        /// Returns true if the subscription has dead letter support on filter evaluation exceptions.
        /// </summary>
        public bool? SubscriptionEnableDeadLetteringOnFilterEvaluationExceptions { get; set; }

        /// <summary>
        /// The number of maximum deliveries.
        /// </summary>
        public int? SubscriptionMaxDeliveryCount { get; set; }

        /// <summary>
        /// Returns true if server-side batched operations are enabled.
        /// </summary>
        public bool? SubscriptionEnableBatchedOperations { get; set; }

        /// <summary>
        /// Returns the status of the subcription (enabled or disabled). When an entity is disabled, that entity cannot send or receive messages.
        /// </summary>
        public EntityStatus? SubscriptionStatus { get; set; }

        /// <summary>
        /// Returns the path to the recipient to which the message is forwarded.
        /// </summary>
        public string SubscriptionForwardTo { get; set; }

        /// <summary>
        /// Returns the path to the recipient to which the dead lettered message is forwarded.
        /// </summary>
        public string SubscriptionForwardDeadLetteredMessagesTo { get; set; }

        /// <summary>
        /// Returns user metadata.
        /// </summary>
        public string SubscriptionUserMetadata { get; set; }

        public RetryPolicy SubscriptionRetryPolicy { get; set; }
    }
}
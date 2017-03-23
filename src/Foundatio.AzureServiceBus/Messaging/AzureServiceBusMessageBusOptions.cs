using System;
using Microsoft.ServiceBus.Messaging;

namespace Foundatio.Messaging {
    public class AzureServiceBusMessageBusOptions : MesssageBusOptions {
        public string ConnectionString { get; set; }
    }

    public class TopicOptions {
        public TimeSpan? AutoDeleteOnIdle { get; set; }

        public TimeSpan? DefaultMessageTimeToLive { get; set; }

        public long? MaxSizeInMegabytes { get; set; }

        public bool? RequiresDuplicateDetection { get; set; }

        public TimeSpan? DuplicateDetectionHistoryTimeWindow { get; set; }

        public bool? EnableBatchedOperations { get; set; }

        public bool? EnableFilteringMessagesBeforePublishing { get; set; }

        public bool? IsAnonymousAccessible { get; set; }

        public EntityStatus? Status { get; set; }

        public string ForwardTo { get; set; }

        public bool? SupportOrdering { get; set; }

        public bool? EnablePartitioning { get; set; }

        public bool? EnableSubscriptionPartitioning { get; set; }

        public bool? EnableExpress { get; set; }

        public string UserMetadata { get; set; }
    }

    public class SubscriptionOptions {
        public TimeSpan? AutoDeleteOnIdle { get; set; }

        public TimeSpan? LockDuration { get; set; }

        public bool? RequiresSession { get; set; }

        public TimeSpan? DefaultMessageTimeToLive { get; set; }

        public bool? EnableDeadLetteringOnMessageExpiration { get; set; }

        public bool? EnableDeadLetteringOnFilterEvaluationExceptions { get; set; }

        public int? MaxDeliveryCount { get; set; }

        public bool? EnableBatchedOperations { get; set; }

        public EntityStatus? Status { get; set; }

        public string ForwardTo { get; set; }

        public string ForwardDeadLetteredMessagesTo { get; set; }

        public string UserMetadata { get; set; }
    }
}
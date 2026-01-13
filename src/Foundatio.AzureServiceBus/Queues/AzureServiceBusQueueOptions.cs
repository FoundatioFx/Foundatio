using System;
using Azure.Core;
using Azure.Messaging.ServiceBus.Administration;

namespace Foundatio.Queues;

public class AzureServiceBusQueueOptions<T> : SharedQueueOptions<T> where T : class
{
    /// <summary>
    /// The connection string to the Azure Service Bus namespace.
    /// </summary>
    public string ConnectionString { get; set; }

    /// <summary>
    /// The fully qualified Service Bus namespace to use for Azure Identity authentication.
    /// Example: "yournamespace.servicebus.windows.net"
    /// </summary>
    public string FullyQualifiedNamespace { get; set; }

    /// <summary>
    /// The token credential to use for Azure Identity authentication.
    /// </summary>
    public TokenCredential Credential { get; set; }

    /// <summary>
    /// Whether the queue can be created if it doesn't exist.
    /// </summary>
    public bool CanCreateQueue { get; set; } = true;

    /// <summary>
    /// The timeout for reading from the queue during dequeue operations.
    /// </summary>
    public TimeSpan ReadQueueTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The interval between dequeue attempts when the queue is empty.
    /// </summary>
    public TimeSpan DequeueInterval { get; set; } = TimeSpan.FromSeconds(1);

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
    /// The function to calculate retry delay based on the attempt number.
    /// </summary>
    public Func<int, TimeSpan> RetryDelay { get; set; } = attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 100));
}

public class AzureServiceBusQueueOptionsBuilder<T> : SharedQueueOptionsBuilder<T, AzureServiceBusQueueOptions<T>, AzureServiceBusQueueOptionsBuilder<T>> where T : class
{
    public AzureServiceBusQueueOptionsBuilder<T> ConnectionString(string connectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        Target.ConnectionString = connectionString;
        return this;
    }

    public AzureServiceBusQueueOptionsBuilder<T> FullyQualifiedNamespace(string fullyQualifiedNamespace)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullyQualifiedNamespace);
        Target.FullyQualifiedNamespace = fullyQualifiedNamespace;
        return this;
    }

    public AzureServiceBusQueueOptionsBuilder<T> Credential(TokenCredential credential)
    {
        Target.Credential = credential ?? throw new ArgumentNullException(nameof(credential));
        return this;
    }

    public AzureServiceBusQueueOptionsBuilder<T> CanCreateQueue(bool enabled)
    {
        Target.CanCreateQueue = enabled;
        return this;
    }

    public AzureServiceBusQueueOptionsBuilder<T> EnableCreateQueue() => CanCreateQueue(true);

    public AzureServiceBusQueueOptionsBuilder<T> DisableCreateQueue() => CanCreateQueue(false);

    public AzureServiceBusQueueOptionsBuilder<T> ReadQueueTimeout(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Read Queue timeout must be greater than or equal to zero.");
        Target.ReadQueueTimeout = timeout;
        return this;
    }

    public AzureServiceBusQueueOptionsBuilder<T> DequeueInterval(TimeSpan interval)
    {
        if (interval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), "Dequeue interval must be greater than or equal to zero.");
        Target.DequeueInterval = interval;
        return this;
    }

    public AzureServiceBusQueueOptionsBuilder<T> RetryDelay(Func<int, TimeSpan> retryDelay)
    {
        Target.RetryDelay = retryDelay ?? throw new ArgumentNullException(nameof(retryDelay));
        return this;
    }

    public AzureServiceBusQueueOptionsBuilder<T> AutoDeleteOnIdle(TimeSpan autoDeleteOnIdle)
    {
        Target.AutoDeleteOnIdle = autoDeleteOnIdle;
        return this;
    }

    public AzureServiceBusQueueOptionsBuilder<T> MaxSizeInMegabytes(long maxSizeInMegabytes)
    {
        Target.MaxSizeInMegabytes = maxSizeInMegabytes;
        return this;
    }

    public AzureServiceBusQueueOptionsBuilder<T> RequiresDuplicateDetection(bool requiresDuplicateDetection)
    {
        Target.RequiresDuplicateDetection = requiresDuplicateDetection;
        return this;
    }

    public AzureServiceBusQueueOptionsBuilder<T> RequiresSession(bool requiresSession)
    {
        Target.RequiresSession = requiresSession;
        return this;
    }

    public AzureServiceBusQueueOptionsBuilder<T> DefaultMessageTimeToLive(TimeSpan defaultMessageTimeToLive)
    {
        Target.DefaultMessageTimeToLive = defaultMessageTimeToLive;
        return this;
    }

    public AzureServiceBusQueueOptionsBuilder<T> EnableDeadLetteringOnMessageExpiration(bool enableDeadLetteringOnMessageExpiration)
    {
        Target.EnableDeadLetteringOnMessageExpiration = enableDeadLetteringOnMessageExpiration;
        return this;
    }

    public AzureServiceBusQueueOptionsBuilder<T> DuplicateDetectionHistoryTimeWindow(TimeSpan duplicateDetectionHistoryTimeWindow)
    {
        Target.DuplicateDetectionHistoryTimeWindow = duplicateDetectionHistoryTimeWindow;
        return this;
    }

    public AzureServiceBusQueueOptionsBuilder<T> EnableBatchedOperations(bool enableBatchedOperations)
    {
        Target.EnableBatchedOperations = enableBatchedOperations;
        return this;
    }

    public AzureServiceBusQueueOptionsBuilder<T> Status(EntityStatus status)
    {
        Target.Status = status;
        return this;
    }

    public AzureServiceBusQueueOptionsBuilder<T> ForwardTo(string forwardTo)
    {
        Target.ForwardTo = forwardTo ?? throw new ArgumentNullException(nameof(forwardTo));
        return this;
    }

    public AzureServiceBusQueueOptionsBuilder<T> ForwardDeadLetteredMessagesTo(string forwardDeadLetteredMessagesTo)
    {
        Target.ForwardDeadLetteredMessagesTo = forwardDeadLetteredMessagesTo ?? throw new ArgumentNullException(nameof(forwardDeadLetteredMessagesTo));
        return this;
    }

    public AzureServiceBusQueueOptionsBuilder<T> EnablePartitioning(bool enablePartitioning)
    {
        Target.EnablePartitioning = enablePartitioning;
        return this;
    }

    public AzureServiceBusQueueOptionsBuilder<T> UserMetadata(string userMetadata)
    {
        Target.UserMetadata = userMetadata ?? throw new ArgumentNullException(nameof(userMetadata));
        return this;
    }
}

using System;
using System.Linq;
using System.Text;
using Azure.Core;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging.Transport;

/// <summary>Settings for <see cref="AzureServiceBusMessageTransport"/>.</summary>
public sealed record AzureServiceBusMessageTransportOptions
{
    /// <summary>
    /// Used by <see cref="AzureServiceBusMessageTransport(AzureServiceBusMessageTransportOptions)"/> to connect, for
    /// example "Endpoint=sb://my-namespace.servicebus.windows.net/;SharedAccessKeyName=...;SharedAccessKey=...". Set this
    /// or <see cref="FullyQualifiedNamespace"/>.
    /// </summary>
    public string? ConnectionString { get; init; }

    /// <summary>
    /// The connection string for creating, deleting and counting queues, topics and subscriptions, when it differs from
    /// <see cref="ConnectionString"/>. When it is not set and <see cref="ConnectionString"/> points at the Service Bus
    /// emulator (<c>UseDevelopmentEmulator=true</c>), the transport uses the same endpoint on port 5300, where the emulator
    /// serves its management API.
    /// </summary>
    public string? AdministrationConnectionString { get; init; }

    /// <summary>
    /// The namespace to connect to with <see cref="Credential"/>, for example "my-namespace.servicebus.windows.net". Set
    /// this or <see cref="ConnectionString"/>.
    /// </summary>
    public string? FullyQualifiedNamespace { get; init; }

    /// <summary>The Microsoft Entra credential used with <see cref="FullyQualifiedNamespace"/>, for example a <c>DefaultAzureCredential</c>.</summary>
    public TokenCredential? Credential { get; init; }

    /// <summary>
    /// Prepended to every queue and topic name, so several environments can share a namespace: queues are named
    /// <c>{Prefix}queue.{name}</c> and topics <c>{Prefix}topic.{name}</c>, as Service Bus queues and topics share one
    /// namespace. Letters, digits, '.', '_' and '-' only, starting with a letter or digit, at most 32 characters.
    /// </summary>
    public string Prefix { get; init; } = String.Empty;

    /// <summary>
    /// Creates every queue and subscription with sessions, so messages with the same
    /// <see cref="OutgoingMessage.GroupKey"/> are delivered in order and never two at a time
    /// (<see cref="TransportCapabilities.OrderedGroups"/>). The group key is the session id.
    /// </summary>
    /// <remarks>
    /// A receiver accepts sessions as it needs them, up to <see cref="MaxConcurrentSessions"/>, and takes no more of a
    /// session while a message it handed out of it is unsettled. Messages without a group key share 8 sessions, which
    /// receivers take many messages from at a time, so at most 8 receivers handle them at once. A delayed message with a
    /// group key, and a retry with a delay, keep their session locked by the receiver that got them until they are due,
    /// renewing the lock, so they hold back their group. Existing entities must match: a queue or subscription without
    /// sessions is refused.
    /// </remarks>
    public bool RequiresSession { get; init; }

    /// <summary>
    /// Creates every queue and topic with duplicate detection, so a repeated <see cref="OutgoingMessage.DeduplicationId"/>
    /// is dropped within <see cref="DuplicateDetectionWindow"/> (<see cref="TransportCapabilities.Deduplication"/>).
    /// The deduplication id becomes the Service Bus message id, and the Foundatio message id travels in an application
    /// property.
    /// </summary>
    public bool RequiresDuplicateDetection { get; init; }

    /// <summary>How long Service Bus remembers message ids for <see cref="RequiresDuplicateDetection"/>: 20 seconds to 7 days.</summary>
    public TimeSpan DuplicateDetectionWindow { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The lock duration of queues and subscriptions the transport creates: 5 seconds to 5 minutes. The broker, not
    /// <see cref="ReceiverOptions.LeaseDuration"/>, decides how long a delivery is leased, and Foundatio renews it a third
    /// of the way through. Entities that already exist keep theirs.
    /// </summary>
    public TimeSpan LockDuration { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The max delivery count of queues and subscriptions the transport creates, after which Service Bus dead-letters a
    /// message itself. Foundatio decides when to dead-letter by its retry policy, so keep this above the attempts it
    /// allows. The Service Bus emulator accepts at most 10.
    /// </summary>
    public int MaxDeliveryCount { get; init; } = Int32.MaxValue;

    /// <summary>
    /// The largest message, in bytes, the namespace accepts: 256 KiB on the Standard tier (the default), up to 100 MiB on
    /// Premium, where queues and topics the transport creates are given this limit. The transport reserves 4 KiB of it
    /// for framing, its own properties and the dead-letter reason, and declares the rest as
    /// <see cref="TransportCapabilities.MaxMessageSize"/>.
    /// </summary>
    public long MaxMessageSize { get; init; } = 256 * 1024;

    /// <summary>
    /// How long an instance subscription survives without a receiver, so one left behind by a stopped process is
    /// removed by Service Bus (<c>AutoDeleteOnIdle</c>). At least 5 minutes, the Service Bus minimum.
    /// </summary>
    public TimeSpan InstanceSubscriptionIdleTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// With <see cref="RequiresSession"/>, how many sessions one receiver hands out messages from at a time. Sessions
    /// held only for a delayed message or retry do not count.
    /// </summary>
    public int MaxConcurrentSessions { get; init; } = 8;

    /// <summary>
    /// When true (the default), queues, topics, subscriptions and their rules are created with the administration client
    /// before they are used, and <see cref="IMessageTransport.DeleteAsync"/> deletes them. Turn it off for applications
    /// without Manage rights: every queue, topic and subscription must then exist, instance subscriptions are not
    /// available, and only dead letters can be deleted.
    /// </summary>
    public bool CreateEntities { get; init; } = true;

    /// <summary>Adjusts the options of the <see cref="ServiceBusClient"/> the transport creates, for example its retry policy or transport type.</summary>
    public Action<ServiceBusClientOptions>? ConfigureClient { get; init; }

    /// <summary>Used to turn <see cref="OutgoingMessage.DeliverAt"/> into a delay and to report lease expiry times.</summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>Receives the transport's logs. Null for no logging.</summary>
    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>Prints the options for <see cref="ToString"/> with the shared access keys and signatures in the connection strings masked.</summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(nameof(ConnectionString)).Append(" = ").Append(RedactSecrets(ConnectionString))
            .Append(", ").Append(nameof(AdministrationConnectionString)).Append(" = ").Append(RedactSecrets(AdministrationConnectionString))
            .Append(", ").Append(nameof(FullyQualifiedNamespace)).Append(" = ").Append(FullyQualifiedNamespace)
            .Append(", ").Append(nameof(Credential)).Append(" = ").Append(Credential?.GetType().Name)
            .Append(", ").Append(nameof(Prefix)).Append(" = ").Append(Prefix)
            .Append(", ").Append(nameof(RequiresSession)).Append(" = ").Append(RequiresSession)
            .Append(", ").Append(nameof(RequiresDuplicateDetection)).Append(" = ").Append(RequiresDuplicateDetection)
            .Append(", ").Append(nameof(DuplicateDetectionWindow)).Append(" = ").Append(DuplicateDetectionWindow)
            .Append(", ").Append(nameof(LockDuration)).Append(" = ").Append(LockDuration)
            .Append(", ").Append(nameof(MaxDeliveryCount)).Append(" = ").Append(MaxDeliveryCount)
            .Append(", ").Append(nameof(MaxMessageSize)).Append(" = ").Append(MaxMessageSize)
            .Append(", ").Append(nameof(InstanceSubscriptionIdleTimeout)).Append(" = ").Append(InstanceSubscriptionIdleTimeout)
            .Append(", ").Append(nameof(MaxConcurrentSessions)).Append(" = ").Append(MaxConcurrentSessions)
            .Append(", ").Append(nameof(CreateEntities)).Append(" = ").Append(CreateEntities)
            .Append(", ").Append(nameof(ConfigureClient)).Append(" = ").Append(ConfigureClient is null ? null : "(set)")
            .Append(", ").Append(nameof(TimeProvider)).Append(" = ").Append(TimeProvider)
            .Append(", ").Append(nameof(LoggerFactory)).Append(" = ").Append(LoggerFactory);
        return true;
    }

    private static string? RedactSecrets(string? connectionString)
    {
        if (String.IsNullOrEmpty(connectionString))
            return connectionString;

        return String.Join(';', connectionString.Split(';').Select(option =>
        {
            int separator = option.IndexOf('=');
            return separator > 0 && IsSecret(option[..separator]) ? option[..(separator + 1)] + "***" : option;
        }));
    }

    /// <summary>The connection string keys Service Bus accepts for a key or signature, ignoring case and spaces.</summary>
    private static bool IsSecret(string name) => name.Replace(" ", String.Empty) is var key
        && (key.Equals("SharedAccessKey", StringComparison.OrdinalIgnoreCase) || key.Equals("SharedAccessSignature", StringComparison.OrdinalIgnoreCase));
}

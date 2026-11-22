using System;
using System.Linq;
using System.Text;
using Azure.Core;
using Azure.Storage.Queues;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging.Transport;

/// <summary>Options for <see cref="AzureStorageQueueMessageTransport"/>.</summary>
public sealed record AzureStorageQueueMessageTransportOptions
{
    /// <summary>
    /// The storage account's connection string, for example <c>UseDevelopmentStorage=true</c> for Azurite. Set this, or
    /// <see cref="ServiceUri"/> with <see cref="Credential"/>.
    /// </summary>
    public string? ConnectionString { get; init; }

    /// <summary>The account's queue endpoint, for example <c>https://myaccount.queue.core.windows.net</c>, used with <see cref="Credential"/>.</summary>
    public Uri? ServiceUri { get; init; }

    /// <summary>
    /// The Microsoft Entra credential used with <see cref="ServiceUri"/>, for example a <c>DefaultAzureCredential</c>.
    /// The identity needs the Storage Queue Data Contributor role, which also lets it create queues.
    /// </summary>
    public TokenCredential? Credential { get; init; }

    /// <summary>
    /// Prepended to every queue name, so several environments can share a storage account. At most 16 lowercase
    /// letters, digits and single hyphens, starting with a letter or digit, for example <c>prod-</c>.
    /// </summary>
    public string Prefix { get; init; } = String.Empty;

    /// <summary>
    /// How long Storage keeps a message nobody received. Null, the default, keeps messages until they are received:
    /// Storage's own default of 7 days would silently drop a backlog a consumer did not get to.
    /// </summary>
    public TimeSpan? MessageTimeToLive { get; init; }

    /// <summary>
    /// How long a receive waits after the first empty poll of its queue. Storage Queues cannot long-poll, so a receive
    /// polls, and each empty poll doubles the wait before the next, up to <see cref="MaxPollInterval"/>; a message
    /// received resets it. Sends and retries this transport makes wake its own waiting receivers at once.
    /// </summary>
    public TimeSpan MinPollInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// The longest a receive waits between polls of an empty queue, and so about the longest a message sent from
    /// another process waits before it is received. Each poll is a billed storage transaction: one idle receiver
    /// polls about 43,000 times a day at 2 seconds.
    /// </summary>
    public TimeSpan MaxPollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long an instance subscription's queue outlives its receiver's last heartbeat before a publish or another
    /// receiver of the topic removes it, after its process died without disposing it. Heartbeats are sent every third
    /// of it.
    /// </summary>
    public TimeSpan InstanceSubscriptionTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Changes the options of the queue clients the transport creates, such as their retry policy or diagnostics.</summary>
    public Action<QueueClientOptions>? ConfigureClient { get; init; }

    /// <summary>The clock for delays and heartbeats. Defaults to <see cref="System.TimeProvider.System"/>.</summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>The logger factory. Defaults to no logging.</summary>
    public ILoggerFactory? LoggerFactory { get; init; }

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(nameof(ConnectionString)).Append(" = ").Append(RedactSecrets(ConnectionString))
            .Append(", ").Append(nameof(ServiceUri)).Append(" = ").Append(ServiceUri is null ? null : new UriBuilder(ServiceUri) { Query = String.Empty }.Uri)
            .Append(", ").Append(nameof(Credential)).Append(" = ").Append(Credential?.GetType().Name)
            .Append(", ").Append(nameof(Prefix)).Append(" = ").Append(Prefix)
            .Append(", ").Append(nameof(MessageTimeToLive)).Append(" = ").Append(MessageTimeToLive)
            .Append(", ").Append(nameof(MinPollInterval)).Append(" = ").Append(MinPollInterval)
            .Append(", ").Append(nameof(MaxPollInterval)).Append(" = ").Append(MaxPollInterval)
            .Append(", ").Append(nameof(InstanceSubscriptionTimeout)).Append(" = ").Append(InstanceSubscriptionTimeout)
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

    private static bool IsSecret(string name) => name.Trim() is var key
        && (key.Equals("AccountKey", StringComparison.OrdinalIgnoreCase) || key.Equals("SharedAccessSignature", StringComparison.OrdinalIgnoreCase));
}

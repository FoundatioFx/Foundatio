using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Foundatio.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// A message transport on Azure Service Bus. A queue is a Service Bus queue; a topic is a topic; a durable subscription
/// is a topic subscription; an instance subscription is a subscription created for its receiver, deleted when the
/// receiver is disposed and removed by Service Bus once idle (<see cref="AzureServiceBusMessageTransportOptions.InstanceSubscriptionIdleTimeout"/>)
/// when its process stops. Dead letters go to each entity's own dead-letter queue, with the reason in
/// <c>DeadLetterReason</c>. Leases are peek-lock locks, whose duration is set per entity, so each delivery reports
/// <see cref="IncomingMessage.LeaseExpiresAt"/>. Entities are created on first use unless
/// <see cref="AzureServiceBusMessageTransportOptions.CreateEntities"/> is off.
/// </summary>
/// <remarks>
/// <para>
/// Service Bus cannot send to one subscription, so a message for one is published to its topic with the subscription's
/// name in <c>x-foundatio-to</c>, and every subscription the transport creates has a rule that matches messages without
/// that property or with its own name. Subscriptions other clients created with other rules do not see the property.
/// </para>
/// <para>
/// Delayed sends and publishes are scheduled messages. Service Bus cannot abandon a message with a delay, so a delayed
/// retry is a scheduled copy carrying the delivery count, and the original is completed: in one transaction on a queue,
/// one after the other on a subscription (a transaction there would span two entities), where a failure in between can
/// deliver the message twice. With <see cref="AzureServiceBusMessageTransportOptions.RequiresSession"/>, a delayed
/// retry instead keeps its session locked until it is due, so it holds back its group.
/// </para>
/// </remarks>
public sealed class AzureServiceBusMessageTransport : IMessageTransport
{
    internal const int MaxBatchSize = 100;
    internal const int EmulatorManagementPort = 5300;
    internal static readonly TimeSpan MaxLockDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MinLockDuration = TimeSpan.FromSeconds(5);
    private const long MinMessageSize = 16 * 1024;

    private readonly bool _ownsClients;
    private readonly ServiceBusAdministrationClient? _admin;
    private readonly ConcurrentDictionary<string, ServiceBusSender> _senders = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<IAsyncDisposable, byte> _receivers = new();
    private int _disposed;

    /// <summary>
    /// Connects with <see cref="AzureServiceBusMessageTransportOptions.ConnectionString"/>, or with
    /// <see cref="AzureServiceBusMessageTransportOptions.FullyQualifiedNamespace"/> and its credential, and disposes the
    /// clients it creates with the transport.
    /// </summary>
    public AzureServiceBusMessageTransport(AzureServiceBusMessageTransportOptions options)
        : this(CreateClient(options), CreateAdministrationClient(options), options, ownsClients: true)
    {
    }

    /// <summary>
    /// Uses the given clients, which the caller owns. <paramref name="administrationClient"/> creates, counts and deletes
    /// entities; it may be null only with <see cref="AzureServiceBusMessageTransportOptions.CreateEntities"/> off, and
    /// then <see cref="GetStatsAsync"/> returns null.
    /// </summary>
    public AzureServiceBusMessageTransport(ServiceBusClient client, ServiceBusAdministrationClient? administrationClient, AzureServiceBusMessageTransportOptions? options = null)
        : this(client, administrationClient, options ?? new AzureServiceBusMessageTransportOptions(), ownsClients: false)
    {
    }

    private AzureServiceBusMessageTransport(ServiceBusClient client, ServiceBusAdministrationClient? administrationClient, AzureServiceBusMessageTransportOptions options, bool ownsClients)
    {
        ArgumentNullException.ThrowIfNull(client);
        ValidateOptions(options);
        if (administrationClient is null && options.CreateEntities)
            throw new ArgumentNullException(nameof(administrationClient), "An administration client is needed to create entities; pass one, or turn CreateEntities off.");

        Client = client;
        _admin = administrationClient;
        _ownsClients = ownsClients;
        Options = options;
        TimeProvider = options.TimeProvider ?? TimeProvider.System;
        Logger = (options.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger<AzureServiceBusMessageTransport>();
        Names = new AzureServiceBusMessageTransportNames(options.Prefix);
        Entities = new AzureServiceBusMessageTransportEntities(administrationClient, options, Logger);
        IsEmulator = ownsClients && IsEmulatorConnectionString(options.ConnectionString);
        Capabilities = CreateCapabilities(options);
    }

    /// <summary>
    /// Sends, publishes and retries with unlimited delays (scheduled messages). Messages may be
    /// <see cref="AzureServiceBusMessageTransportOptions.MaxMessageSize"/> less 4 KiB, which the transport keeps for framing,
    /// its properties and the dead-letter reason; headers too large for Service Bus's 64 KiB of application properties travel
    /// in the body. Retries shorter than a second are held by Foundatio, as a scheduled copy costs a send. Leases expire
    /// with the entity's lock (at most 5 minutes, renewed by Foundatio). Sessions order groups; duplicate detection
    /// deduplicates.
    /// </summary>
    public TransportCapabilities Capabilities { get; }

    internal AzureServiceBusMessageTransportOptions Options { get; }
    internal ServiceBusClient Client { get; }
    internal AzureServiceBusMessageTransportNames Names { get; }
    internal AzureServiceBusMessageTransportEntities Entities { get; }
    internal TimeProvider TimeProvider { get; }
    internal ILogger Logger { get; }
    internal bool IsEmulator { get; }
    internal bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    /// <inheritdoc />
    public async Task SendAsync(MessageDestination destination, IReadOnlyList<OutgoingMessage> messages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(messages);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (messages.Count == 0)
            return;

        foreach (var message in messages)
        {
            ArgumentNullException.ThrowIfNull(message, nameof(messages));
            CheckSize(message);
        }

        try
        {
            string entityPath;
            string? target = null;
            switch (destination.Kind)
            {
                case MessageDestinationKind.Queue:
                    entityPath = Names.Queue(destination.Name);
                    await Entities.EnsureQueueAsync(entityPath, cancellationToken).AnyContext();
                    break;
                case MessageDestinationKind.Subscription:
                    entityPath = Names.Topic(destination.Name);
                    target = AzureServiceBusMessageTransportNames.Subscription(destination.SubscriptionName!);
                    await Entities.EnsureSubscriptionAsync(entityPath, target, cancellationToken).AnyContext();
                    break;
                default:
                    entityPath = Names.Topic(destination.Name);
                    await Entities.EnsureTopicAsync(entityPath, cancellationToken).AnyContext();
                    break;
            }

            var options = new AzureServiceBusEncodeOptions(TimeProvider.GetUtcNow(), Options.RequiresSession, Options.RequiresDuplicateDetection, target);
            var encoded = messages.Select(m => AzureServiceBusMessageTransportCodec.Encode(m, options)).ToList();
            await SendEncodedAsync(destination, entityPath, encoded, cancellationToken).AnyContext();
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessageSizeExceeded)
        {
            throw new MessageTooLargeException($"A message sent to {destination} is larger than Service Bus accepts once encoded: its headers need more framing than the transport reserves. {ex.Message}", ex)
            {
                Limit = Capabilities.MaxMessageSize
            };
        }
        catch (ServiceBusException ex)
        {
            throw new MessageBusException($"Could not send to {destination}: {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new MessageBusException($"Could not send to {destination}: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public async Task<IMessageReceiver> CreateReceiverAsync(MessageSource source, ReceiverOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.LeaseDuration, TimeSpan.Zero, nameof(options));
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        AzureServiceBusReceiverEntity entity;
        try
        {
            entity = await ResolveEntityAsync(source, cancellationToken).AnyContext();
        }
        catch (ServiceBusException ex)
        {
            throw new MessageBusException($"Could not open a receiver for {source}: {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new MessageBusException($"Could not open a receiver for {source}: {ex.Message}", ex);
        }

        IAsyncDisposable receiver;
        if (Options.RequiresSession && !entity.DeadLetter)
        {
            var sessionReceiver = new AzureServiceBusSessionReceiver(this, source, entity);
            sessionReceiver.Start();
            receiver = sessionReceiver;
        }
        else
        {
            receiver = new AzureServiceBusMessageReceiver(this, source, entity);
        }

        _receivers.TryAdd(receiver, 0);
        if (IsDisposed)
        {
            await receiver.DisposeAsync().AnyContext();
            ThrowIfDisposed();
        }

        Logger.LogDebug("Opened a receiver for {Source} on {Entity}", source, entity);
        return (IMessageReceiver)receiver;
    }

    /// <summary>
    /// Deletes the queue or subscription with the administration client (Service Bus deletes its dead letters with it),
    /// or, for a dead-letter source, receives and deletes the dead letters that are not locked by a receiver. Deleting
    /// queues and subscriptions needs <see cref="AzureServiceBusMessageTransportOptions.CreateEntities"/>. Topics are
    /// kept: other services may still publish to them.
    /// </summary>
    public async Task DeleteAsync(MessageSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfDisposed();
        if (source.Kind == MessageSourceKind.InstanceSubscription)
            throw new ArgumentException("Instance subscriptions are removed by disposing their receiver.", nameof(source));

        try
        {
            switch (source.Kind)
            {
                case MessageSourceKind.Queue:
                    await Entities.DeleteQueueAsync(Names.Queue(source.Name), cancellationToken).AnyContext();
                    break;
                case MessageSourceKind.Subscription:
                    await Entities.DeleteSubscriptionAsync(Names.Topic(source.Topic!), AzureServiceBusMessageTransportNames.Subscription(source.Name), cancellationToken).AnyContext();
                    break;
                default:
                    await PurgeDeadLettersAsync(source.DeadLetterOf!, cancellationToken).AnyContext();
                    break;
            }
        }
        catch (ServiceBusException ex)
        {
            throw new MessageBusException($"Could not delete {source}: {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new MessageBusException($"Could not delete {source}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Reads the entity's runtime properties with one administration call and creates nothing. Service Bus counts a
    /// locked message as active, so <see cref="MessageSourceStats.InFlight"/> is null and messages being handled count as
    /// <see cref="MessageSourceStats.Queued"/>, as do messages a session receiver holds until they are due.
    /// <see cref="MessageSourceStats.Delayed"/> counts a queue's scheduled messages, including delayed retries; it is null
    /// for subscriptions, whose scheduled messages wait in the topic, which counts them for all of its subscriptions
    /// together. Returns null for instance subscriptions, without an administration client, and on the Service Bus
    /// emulator, whose runtime properties always report zero.
    /// </summary>
    /// <exception cref="ArgumentException">The source is a dead-letter source.</exception>
    public async Task<MessageSourceStats?> GetStatsAsync(MessageSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Kind == MessageSourceKind.DeadLetter)
            throw new ArgumentException("A dead-letter queue is counted as the DeadLettered count of the source it belongs to.", nameof(source));

        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (source.Kind == MessageSourceKind.InstanceSubscription || _admin is null || IsEmulator)
            return null;

        try
        {
            if (source.Kind == MessageSourceKind.Queue)
            {
                var (active, scheduled, deadLettered) = await Entities.GetQueueCountsAsync(Names.Queue(source.Name), cancellationToken).AnyContext();
                return new MessageSourceStats { Source = source, Queued = active, Delayed = scheduled, InFlight = null, DeadLettered = deadLettered };
            }

            var (subscriptionActive, subscriptionDeadLettered) = await Entities.GetSubscriptionCountsAsync(Names.Topic(source.Topic!),
                AzureServiceBusMessageTransportNames.Subscription(source.Name), cancellationToken).AnyContext();
            return new MessageSourceStats { Source = source, Queued = subscriptionActive, Delayed = null, InFlight = null, DeadLettered = subscriptionDeadLettered };
        }
        catch (ServiceBusException ex)
        {
            throw new MessageBusException($"Could not read the counts of {source}: {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new MessageBusException($"Could not read the counts of {source}: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        await Task.WhenAll(_receivers.Keys.Select(async receiver =>
        {
            try
            {
                await receiver.DisposeAsync().AnyContext();
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Could not dispose a receiver");
            }
        })).AnyContext();

        foreach (var sender in _senders.Values)
        {
            try
            {
                await sender.DisposeAsync().AnyContext();
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Could not close the sender for {Entity}", sender.EntityPath);
            }
        }

        if (_ownsClients)
            await Client.DisposeAsync().AnyContext();
    }

    internal void Untrack(IAsyncDisposable receiver) => _receivers.TryRemove(receiver, out _);

    /// <summary>
    /// Retries a delivery after <paramref name="delay"/>: schedules a copy carrying its delivery count, and completes the
    /// original, in one transaction on a queue. On a subscription the copy goes to the topic for this subscription only,
    /// and the two happen one after the other, since a transaction there would span two entities.
    /// </summary>
    internal async Task ScheduleRetryAsync(ServiceBusReceiver receiver, AzureServiceBusReceiverEntity entity, AzureServiceBusIncomingMessage delivery, TimeSpan delay,
        MessageSource source, CancellationToken cancellationToken)
    {
        var copy = AzureServiceBusMessageTransportCodec.EncodeRetry(delivery.Received, delivery.DeliveryCount, DateTimeOffset.UtcNow + delay, entity.Subscription, Options.RequiresDuplicateDetection);
        var sender = GetSender(entity.SendPath);
        if (entity.Queue is not null)
        {
            try
            {
                using var transaction = new TransactionScope(TransactionScopeOption.RequiresNew, TransactionScopeAsyncFlowOption.Enabled);
                await sender.SendMessageAsync(copy, cancellationToken).AnyContext();
                await receiver.CompleteMessageAsync(delivery.Received, cancellationToken).AnyContext();
                transaction.Complete();
            }
            catch (TransactionException ex)
            {
                throw new MessageBusException($"Could not schedule the retry of message {delivery.MessageId} from {source}: {ex.Message}", ex);
            }

            return;
        }

        await sender.SendMessageAsync(copy, cancellationToken).AnyContext();
        try
        {
            await receiver.CompleteMessageAsync(delivery.Received, cancellationToken).AnyContext();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Scheduled the retry of message {MessageId} from {Source} but could not complete the original; it will be delivered twice", delivery.MessageId, source);
            throw;
        }
    }

    internal ServiceBusSender GetSender(string entityPath) => _senders.GetOrAdd(entityPath, static (path, client) => client.CreateSender(path), Client);

    /// <summary>
    /// When a lease ends on this transport's clock: the broker's lock expiry moved onto <see cref="TimeProvider"/>, and,
    /// when the entity's lock duration is known, no later than that long after the request started, so a clock that is
    /// behind the broker's does not make Foundatio renew too late.
    /// </summary>
    internal DateTimeOffset GetLeaseEnd(DateTimeOffset requestedAt, DateTimeOffset lockedUntil, TimeSpan? lockDuration)
    {
        var mapped = TimeProvider.GetUtcNow() + (lockedUntil - DateTimeOffset.UtcNow);
        return lockDuration is { } duration && requestedAt + duration < mapped ? requestedAt + duration : mapped;
    }

    /// <summary>Errors after which a receiver can never deliver again: its entity was deleted or disabled.</summary>
    internal static bool IsFatal(ServiceBusException ex) =>
        ex.Reason is ServiceBusFailureReason.MessagingEntityNotFound or ServiceBusFailureReason.MessagingEntityDisabled;

    internal static bool IsLockLost(ServiceBusException ex) =>
        ex.Reason is ServiceBusFailureReason.MessageLockLost or ServiceBusFailureReason.SessionLockLost;

    /// <summary>The management endpoint for a connection string: the emulator serves it on port 5300 of the same host.</summary>
    internal static string GetAdministrationConnectionString(string connectionString)
    {
        if (!IsEmulatorConnectionString(connectionString))
            return connectionString;

        return String.Join(';', connectionString.Split(';').Select(option =>
        {
            int separator = option.IndexOf('=');
            if (separator <= 0 || !option[..separator].Trim().Equals("Endpoint", StringComparison.OrdinalIgnoreCase)
                || !Uri.TryCreate(option[(separator + 1)..].Trim(), UriKind.Absolute, out var endpoint))
            {
                return option;
            }

            return $"{option[..separator]}={new UriBuilder(endpoint) { Port = EmulatorManagementPort, Path = String.Empty }.Uri.ToString().TrimEnd('/')}";
        }));
    }

    internal static bool IsEmulatorConnectionString(string? connectionString) =>
        connectionString is not null && connectionString.Replace(" ", String.Empty).Contains("UseDevelopmentEmulator=true", StringComparison.OrdinalIgnoreCase);

    private async Task<AzureServiceBusReceiverEntity> ResolveEntityAsync(MessageSource source, CancellationToken cancellationToken)
    {
        switch (source.Kind)
        {
            case MessageSourceKind.Queue:
                {
                    string queue = Names.Queue(source.Name);
                    return AzureServiceBusReceiverEntity.ForQueue(queue, await Entities.EnsureQueueAsync(queue, cancellationToken).AnyContext());
                }
            case MessageSourceKind.Subscription:
                {
                    string topic = Names.Topic(source.Topic!);
                    string subscription = AzureServiceBusMessageTransportNames.Subscription(source.Name);
                    return AzureServiceBusReceiverEntity.ForSubscription(topic, subscription, await Entities.EnsureSubscriptionAsync(topic, subscription, cancellationToken).AnyContext());
                }
            case MessageSourceKind.InstanceSubscription:
                {
                    string topic = Names.Topic(source.Topic!);
                    string subscription = AzureServiceBusMessageTransportNames.NewInstance(source.Name);
                    var lockDuration = await Entities.CreateInstanceSubscriptionAsync(topic, subscription, cancellationToken).AnyContext();
                    return AzureServiceBusReceiverEntity.ForSubscription(topic, subscription, lockDuration, instance: true);
                }
            default:
                {
                    var of = await ResolveEntityAsync(source.DeadLetterOf!, cancellationToken).AnyContext();
                    return of with { DeadLetter = true };
                }
        }
    }

    /// <summary>Sends in batches of at most <see cref="MaxBatchSize"/> that fit the link's size limit, creating the entity again once if it was deleted.</summary>
    private async Task SendEncodedAsync(MessageDestination destination, string entityPath, IReadOnlyList<ServiceBusMessage> messages, CancellationToken cancellationToken)
    {
        int index = 0;
        bool recreated = false;
        while (index < messages.Count)
        {
            var sender = GetSender(entityPath);
            try
            {
                using var batch = await sender.CreateMessageBatchAsync(cancellationToken).ConfigureAwait(false);
                int start = index;
                while (index < messages.Count && batch.Count < MaxBatchSize && batch.TryAddMessage(messages[index]))
                    index++;

                if (index == start)
                {
                    // Too large for a batch's framing, which a single send does not need.
                    await sender.SendMessageAsync(messages[index], cancellationToken).AnyContext();
                    index++;
                }
                else
                {
                    await sender.SendMessagesAsync(batch, cancellationToken).AnyContext();
                }
            }
            catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityNotFound && !recreated && Options.CreateEntities)
            {
                recreated = true;
                Logger.LogDebug("{Entity} no longer exists; creating it again", entityPath);
                if (destination.Kind == MessageDestinationKind.Queue)
                {
                    Entities.ForgetQueue(entityPath);
                    await Entities.EnsureQueueAsync(entityPath, cancellationToken).AnyContext();
                }
                else
                {
                    Entities.ForgetTopic(entityPath);
                    if (destination.Kind == MessageDestinationKind.Subscription)
                        await Entities.EnsureSubscriptionAsync(entityPath, AzureServiceBusMessageTransportNames.Subscription(destination.SubscriptionName!), cancellationToken).AnyContext();
                    else
                        await Entities.EnsureTopicAsync(entityPath, cancellationToken).AnyContext();
                }
            }
        }
    }

    private async Task PurgeDeadLettersAsync(MessageSource of, CancellationToken cancellationToken)
    {
        var entity = of.Kind == MessageSourceKind.Queue
            ? AzureServiceBusReceiverEntity.ForQueue(Names.Queue(of.Name), null, deadLetter: true)
            : AzureServiceBusReceiverEntity.ForSubscription(Names.Topic(of.Topic!), AzureServiceBusMessageTransportNames.Subscription(of.Name), null, deadLetter: true);

        await using var receiver = entity.CreateReceiver(Client, ServiceBusReceiveMode.ReceiveAndDelete);
        int deleted = 0;
        try
        {
            while (true)
            {
                var batch = await receiver.ReceiveMessagesAsync(MaxBatchSize, TimeSpan.FromSeconds(1), cancellationToken).AnyContext();
                if (batch.Count == 0)
                    break;

                deleted += batch.Count;
            }
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityNotFound)
        {
        }

        Logger.LogDebug("Deleted {Count} dead letters of {Source}", deleted, of);
    }

    private void CheckSize(OutgoingMessage message)
    {
        long size = MessageSize.Of(message);
        if (size > Capabilities.MaxMessageSize)
        {
            throw new MessageTooLargeException($"Message {message.MessageId} is {size} bytes with its headers and ids, which exceeds the Service Bus transport limit of {Capabilities.MaxMessageSize} bytes.")
            {
                Size = size,
                Limit = Capabilities.MaxMessageSize
            };
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);

    private static TransportCapabilities CreateCapabilities(AzureServiceBusMessageTransportOptions options) => new()
    {
        MaxDelay = null,
        MaxPublishDelay = null,
        MaxRetryDelay = null,
        // A delayed retry is a scheduled copy, or holds its session, which costs requests; shorter ones are cheaper held
        // in-process.
        MinRetryDelay = TimeSpan.FromSeconds(1),
        MaxSendBatchSize = MaxBatchSize,
        MaxReceiveBatchSize = MaxBatchSize,
        MaxMessageSize = options.MaxMessageSize - AzureServiceBusMessageTransportCodec.ReservedBytes,
        MaxLeaseDuration = MaxLockDuration,
        LeaseExpiration = true,
        OrderedGroups = options.RequiresSession,
        Deduplication = options.RequiresDuplicateDetection
    };

    private static void ValidateOptions(AzureServiceBusMessageTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        AzureServiceBusMessageTransportNames.ValidatePrefix(options.Prefix, nameof(options));
        if (options.LockDuration < MinLockDuration || options.LockDuration > MaxLockDuration)
            throw new ArgumentOutOfRangeException(nameof(options), options.LockDuration, "LockDuration must be between 5 seconds and 5 minutes.");
        if (options.MaxDeliveryCount < 1)
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxDeliveryCount, "MaxDeliveryCount must be at least 1.");
        if (options.MaxMessageSize < MinMessageSize)
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxMessageSize, $"MaxMessageSize must be at least {MinMessageSize} bytes.");
        if (options.InstanceSubscriptionIdleTimeout < TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(options), options.InstanceSubscriptionIdleTimeout, "InstanceSubscriptionIdleTimeout must be at least 5 minutes, the shortest AutoDeleteOnIdle Service Bus allows.");
        if (options.DuplicateDetectionWindow < TimeSpan.FromSeconds(20) || options.DuplicateDetectionWindow > TimeSpan.FromDays(7))
            throw new ArgumentOutOfRangeException(nameof(options), options.DuplicateDetectionWindow, "DuplicateDetectionWindow must be between 20 seconds and 7 days.");
        if (options.MaxConcurrentSessions < 1)
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxConcurrentSessions, "MaxConcurrentSessions must be at least 1.");
    }

    private static ServiceBusClient CreateClient(AzureServiceBusMessageTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var clientOptions = new ServiceBusClientOptions();
        options.ConfigureClient?.Invoke(clientOptions);

        if (!String.IsNullOrEmpty(options.ConnectionString))
        {
            if (!String.IsNullOrEmpty(options.FullyQualifiedNamespace))
                throw new ArgumentException("Set ConnectionString or FullyQualifiedNamespace, not both.", nameof(options));

            return new ServiceBusClient(options.ConnectionString, clientOptions);
        }

        if (String.IsNullOrEmpty(options.FullyQualifiedNamespace))
            throw new ArgumentException("ConnectionString or FullyQualifiedNamespace is required.", nameof(options));
        if (options.Credential is null)
            throw new ArgumentException("A Credential is required with FullyQualifiedNamespace.", nameof(options));

        return new ServiceBusClient(options.FullyQualifiedNamespace, options.Credential, clientOptions);
    }

    private static ServiceBusAdministrationClient? CreateAdministrationClient(AzureServiceBusMessageTransportOptions options)
    {
        if (!String.IsNullOrEmpty(options.AdministrationConnectionString))
            return new ServiceBusAdministrationClient(options.AdministrationConnectionString);
        if (!String.IsNullOrEmpty(options.ConnectionString))
            return new ServiceBusAdministrationClient(GetAdministrationConnectionString(options.ConnectionString));

        return new ServiceBusAdministrationClient(options.FullyQualifiedNamespace, options.Credential);
    }
}

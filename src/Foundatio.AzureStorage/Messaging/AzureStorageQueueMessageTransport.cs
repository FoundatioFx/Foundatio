using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Foundatio.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// A message transport on Azure Storage Queues. A queue is a Storage queue. Storage has no topics, so a durable
/// subscription is a queue of its own, named after its topic, and a publish lists the topic's subscription queues (one
/// call, not cached, so a subscription another process just opened gets every later publish) and sends a copy to each.
/// An instance subscription is a queue per receiver, deleted when the receiver is disposed, or by a later publish or
/// instance receiver of the topic once its heartbeat is older than
/// <see cref="AzureStorageQueueMessageTransportOptions.InstanceSubscriptionTimeout"/>. Every queue and durable
/// subscription has a dead-letter queue named after it with <c>-poison</c>. Queues are created on first use.
/// </summary>
/// <remarks>
/// <para>
/// Leases are visibility timeouts: a received message is invisible for the lease, and a renewal or abandon updates the
/// message, which gives it a new pop receipt that later settlements use. A settlement through a pop receipt the message
/// no longer has (it was received again) throws <see cref="MessageLeaseLostException"/>. Delays are visibility
/// timeouts too, of up to 7 days, for sends, publishes and retries.
/// </para>
/// <para>
/// Storage cannot long-poll, so a receive polls its queue, waiting longer after each empty poll, from
/// <see cref="AzureStorageQueueMessageTransportOptions.MinPollInterval"/> to
/// <see cref="AzureStorageQueueMessageTransportOptions.MaxPollInterval"/>. A message sent, or a retry made, through
/// this transport wakes its own receivers of that queue at once; one sent from another process is received on the next poll.
/// </para>
/// <para>
/// Deleting a source clears its queue and dead-letter queue but keeps them, since Storage does not let a deleted
/// queue's name be used again for at least 30 seconds. A deleted durable subscription is marked as unsubscribed, so
/// publishes skip it until a receiver opens or a message is sent to it again; a receiver still open on it notices within
/// seconds of receiving nothing and throws <see cref="MessageReceiverFaultedException"/>, so its replacement subscribes again.
/// </para>
/// </remarks>
public sealed class AzureStorageQueueMessageTransport : IMessageTransport
{
    internal const int MaxReceiveBatchSize = 32;
    internal const string KindMetadata = "foundatio_kind";
    internal const string TopicMetadata = "foundatio_topic";
    internal const string NameMetadata = "foundatio_name";
    internal const string HeartbeatMetadata = "foundatio_heartbeat";
    internal const string QueueKind = "queue";
    internal const string SubscriptionKind = "subscription";
    internal const string InstanceKind = "instance";
    internal const string DeadLetterKind = "deadletter";

    /// <summary>The longest visibility timeout Storage takes, which bounds delays, retries and leases.</summary>
    internal static readonly TimeSpan MaxVisibilityTimeout = TimeSpan.FromDays(7);

    /// <summary>How many requests one send, publish or release makes at once: Storage has no batch send or delete.</summary>
    internal const int MaxRequestsInFlight = 32;

    private const int MaxSendBatchSize = 100;
    private const int MaxConcurrentReceives = 8;
    private const int MaxClearAttempts = 10;

    private readonly AzureStorageQueueMessageTransportNames _names;
    private readonly ConcurrentDictionary<string, QueueClient> _queues = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AzureStorageQueueSignal> _signals = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _lastSweeps = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<AzureStorageQueueMessageReceiver, byte> _receivers = new();
    private int _disposed;

    /// <summary>Connects with <see cref="AzureStorageQueueMessageTransportOptions.ConnectionString"/> or <see cref="AzureStorageQueueMessageTransportOptions.ServiceUri"/>.</summary>
    public AzureStorageQueueMessageTransport(AzureStorageQueueMessageTransportOptions options)
        : this(CreateService(options), options)
    {
    }

    /// <summary>
    /// Uses <paramref name="service"/>, which the caller owns. Its clients must not base64-encode messages
    /// (<see cref="QueueClientOptions.MessageEncoding"/> <see cref="QueueMessageEncoding.None"/>, the default): the
    /// transport encodes them itself. Connection options in <paramref name="options"/> are ignored.
    /// </summary>
    public AzureStorageQueueMessageTransport(QueueServiceClient service, AzureStorageQueueMessageTransportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        Options = options ?? new AzureStorageQueueMessageTransportOptions();
        AzureStorageQueueMessageTransportNames.ValidatePrefix(Options.Prefix, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Options.MinPollInterval, TimeSpan.Zero, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(Options.MaxPollInterval, Options.MinPollInterval, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(Options.InstanceSubscriptionTimeout, TimeSpan.FromSeconds(1), nameof(options));
        if (Options.MessageTimeToLive is { } ttl && ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), ttl, "MessageTimeToLive must be positive, or null to keep messages until they are received.");

        Service = service;
        _names = new AzureStorageQueueMessageTransportNames(Options.Prefix);
        TimeProvider = Options.TimeProvider ?? TimeProvider.System;
        Logger = (Options.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger<AzureStorageQueueMessageTransport>();
        TimeToLive = Options.MessageTimeToLive ?? TimeSpan.FromSeconds(-1);

        var maxDelay = Options.MessageTimeToLive is { } timeToLive && timeToLive <= MaxVisibilityTimeout ? timeToLive - TimeSpan.FromSeconds(1) : MaxVisibilityTimeout;
        Capabilities = new TransportCapabilities
        {
            MaxDelay = maxDelay,
            MinRetryDelay = TimeSpan.FromSeconds(1), // visibility timeouts are whole seconds
            MaxSendBatchSize = MaxSendBatchSize,
            MaxReceiveBatchSize = MaxReceiveBatchSize,
            MaxConcurrentReceives = MaxConcurrentReceives,
            ReceiveAhead = true,
            MaxMessageSize = AzureStorageQueueMessageTransportCodec.MaxMessageSize,
            MaxLeaseDuration = MaxVisibilityTimeout,
            LeaseExpiration = true
        };
    }

    /// <summary>
    /// Sends, publishes and retries can be delayed by up to 7 days (less than <see cref="AzureStorageQueueMessageTransportOptions.MessageTimeToLive"/>
    /// when that is shorter); Foundatio sends longer delays again. Messages may be 46 KiB as <see cref="MessageSize"/>
    /// measures them: Storage takes 64 KiB of text, and the transport base64-encodes its binary envelope, keeping 2 KiB
    /// for framing and the dead-letter reason. A receive returns up to 32 messages, and Foundatio may run 8 at once on
    /// one receiver. Groups are not ordered and nothing is deduplicated.
    /// </summary>
    public TransportCapabilities Capabilities { get; }

    internal AzureStorageQueueMessageTransportOptions Options { get; }
    internal QueueServiceClient Service { get; }
    internal TimeProvider TimeProvider { get; }
    internal ILogger Logger { get; }
    internal TimeSpan TimeToLive { get; }
    internal bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    /// <inheritdoc />
    async Task IMessageTransport.SendAsync(MessageDestination destination, IReadOnlyList<OutgoingMessage> messages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(messages);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (messages.Count == 0)
            return;

        var now = TimeProvider.GetUtcNow();
        var encoded = new AzureStorageQueueEncodedMessage[messages.Count];
        for (int i = 0; i < messages.Count; i++)
            encoded[i] = Encode(messages[i], now);

        try
        {
            switch (destination.Kind)
            {
                case MessageDestinationKind.Queue:
                    string queueName = _names.Queue(destination.Name);
                    await SendToQueueAsync(queueName, encoded, () => QueueMetadata(QueueKind, destination.Name), cancellationToken).AnyContext();
                    break;
                case MessageDestinationKind.Subscription:
                    string subscriptionName = await SubscribeAsync(destination.Name, destination.SubscriptionName!, cancellationToken).AnyContext();
                    await SendToQueueAsync(subscriptionName, encoded, null, cancellationToken).AnyContext();
                    break;
                default:
                    var subscriptions = await GetSubscriptionQueuesAsync(destination.Name, cancellationToken).AnyContext();
                    if (subscriptions.Count == 1)
                    {
                        await SendToQueueAsync(subscriptions[0], encoded, null, cancellationToken).AnyContext();
                    }
                    else if (subscriptions.Count > 1)
                    {
                        await Parallel.ForEachAsync(subscriptions, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, MaxRequestsInFlight / encoded.Length), CancellationToken = cancellationToken },
                            (queue, token) => new ValueTask(SendToQueueAsync(queue, encoded, null, token))).AnyContext();
                    }

                    break;
            }
        }
        catch (RequestFailedException ex)
        {
            throw new MessageBusException($"Could not send to {destination}: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    async Task<IMessageReceiver> IMessageTransport.CreateReceiverAsync(MessageSource source, ReceiverOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.LeaseDuration, TimeSpan.Zero, nameof(options));
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        AzureStorageQueueMessageReceiver receiver;
        try
        {
            switch (source.Kind)
            {
                case MessageSourceKind.InstanceSubscription:
                    string instanceName = _names.Instance(source.Topic!, source.Name);
                    await GetQueue(instanceName).CreateAsync(InstanceMetadata(source.Topic!), cancellationToken).AnyContext();
                    Logger.LogDebug("Created instance subscription queue {QueueName} for topic {Topic}", instanceName, source.Topic);
                    receiver = new AzureStorageQueueMessageReceiver(this, source, options, instanceName, null);
                    break;
                case MessageSourceKind.Subscription:
                    string subscriptionName = await SubscribeAsync(source.Topic!, source.Name, cancellationToken).AnyContext();
                    receiver = new AzureStorageQueueMessageReceiver(this, source, options, subscriptionName, _names.DeadLetter(source));
                    break;
                case MessageSourceKind.DeadLetter:
                    string deadLetterName = _names.DeadLetter(source);
                    await GetQueue(deadLetterName).CreateIfNotExistsAsync(DeadLetterMetadata(source.DeadLetterOf!), cancellationToken).AnyContext();
                    receiver = new AzureStorageQueueMessageReceiver(this, source, options, deadLetterName, null);
                    break;
                default:
                    string queueName = _names.Queue(source.Name);
                    await GetQueue(queueName).CreateIfNotExistsAsync(QueueMetadata(QueueKind, source.Name), cancellationToken).AnyContext();
                    receiver = new AzureStorageQueueMessageReceiver(this, source, options, queueName, _names.DeadLetter(source));
                    break;
            }
        }
        catch (RequestFailedException ex)
        {
            throw new MessageBusException($"Could not open a receiver for {source}: {ex.Message}", ex);
        }

        _receivers.TryAdd(receiver, 0);
        receiver.Start();
        if (source.Kind == MessageSourceKind.InstanceSubscription)
            _ = SweepInstancesAsync(source.Topic!);

        return receiver;
    }

    /// <summary>
    /// Reads the approximate message counts Storage keeps for the source's queue and its dead-letter queue, with one
    /// call each, and creates neither. Storage counts every message a queue holds, visible or not, so
    /// <see cref="MessageSourceStats.Queued"/> includes messages being handled and delayed messages, and
    /// <see cref="MessageSourceStats.InFlight"/> and <see cref="MessageSourceStats.Delayed"/> are null. Instance
    /// subscriptions are not counted (null): each receiver has its own queue.
    /// </summary>
    /// <exception cref="ArgumentException">The source is a dead-letter source.</exception>
    async Task<MessageSourceStats?> IMessageTransport.GetStatsAsync(MessageSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Kind == MessageSourceKind.DeadLetter)
            throw new ArgumentException("A dead-letter queue is counted as the DeadLettered count of the source it belongs to.", nameof(source));

        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (source.Kind == MessageSourceKind.InstanceSubscription)
            return null;

        try
        {
            var queued = GetCountAsync(_names.Source(source), cancellationToken);
            var deadLettered = GetCountAsync(_names.DeadLetter(source), cancellationToken);
            await Task.WhenAll(queued, deadLettered).AnyContext();
            return new MessageSourceStats { Source = source, Queued = await queued.AnyContext(), DeadLettered = await deadLettered.AnyContext() };
        }
        catch (RequestFailedException ex)
        {
            throw new MessageBusException($"Could not read the counts of {source}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Removes every message of the source's queue and dead-letter queue, delayed and leased ones included, and keeps
    /// the queues: Storage does not let a deleted queue's name be used again for at least 30 seconds. A durable
    /// subscription is marked as unsubscribed first, so publishes skip it until it is received from or sent to again.
    /// Messages sent to the queue while it is cleared may be removed too.
    /// </summary>
    async Task IMessageTransport.DeleteAsync(MessageSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfDisposed();
        if (source.Kind == MessageSourceKind.InstanceSubscription)
            throw new ArgumentException("Instance subscriptions are removed by disposing their receiver.", nameof(source));

        try
        {
            if (source.Kind == MessageSourceKind.Subscription)
            {
                string queueName = _names.Subscription(source.Topic!, source.Name);
                try
                {
                    await GetQueue(queueName).SetMetadataAsync(QueueMetadata(SubscriptionKind, source.Name), cancellationToken).AnyContext();
                }
                catch (RequestFailedException ex) when (IsQueueNotFound(ex))
                {
                }
            }

            if (source.Kind != MessageSourceKind.DeadLetter)
                await ClearAsync(_names.Source(source), cancellationToken).AnyContext();

            await ClearAsync(_names.DeadLetter(source), cancellationToken).AnyContext();
        }
        catch (RequestFailedException ex)
        {
            throw new MessageBusException($"Could not delete {source}: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        await Task.WhenAll(_receivers.Keys.Select(r => r.DisposeAsync().AsTask())).AnyContext();
        _signals.Clear();
    }

    internal QueueClient GetQueue(string name) => _queues.GetOrAdd(name, static (n, service) => service.GetQueueClient(n), Service);

    internal AzureStorageQueueSignal GetSignal(string queueName) => _signals.GetOrAdd(queueName, static _ => new AzureStorageQueueSignal());

    internal void Untrack(AzureStorageQueueMessageReceiver receiver) => _receivers.TryRemove(receiver, out _);

    /// <summary>Wakes this transport's receivers of the queue after a message became visible in it.</summary>
    internal void Signal(string queueName)
    {
        if (_signals.TryGetValue(queueName, out var signal))
            signal.Set();
    }

    /// <summary>Wakes this transport's receivers of the queue when a message sent or retried with a delay becomes visible.</summary>
    internal void SignalAfter(string queueName, TimeSpan delay)
    {
        if (delay > Options.MaxPollInterval * 30 || !_signals.ContainsKey(queueName))
            return;

        _ = Task.Delay(delay, TimeProvider).ContinueWith(static (_, state) =>
        {
            var (transport, name) = ((AzureStorageQueueMessageTransport, string))state!;
            transport.Signal(name);
        }, (this, queueName), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>The visibility timeout that makes a message visible after <paramref name="delay"/>: whole seconds, up to 7 days.</summary>
    internal static TimeSpan ToVisibilityTimeout(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
            return TimeSpan.Zero;

        double seconds = Math.Ceiling(delay.TotalSeconds);
        return seconds >= MaxVisibilityTimeout.TotalSeconds ? MaxVisibilityTimeout : TimeSpan.FromSeconds(seconds);
    }

    /// <summary>A pop receipt Storage no longer accepts: the message was received again, cleared or completed.</summary>
    internal static bool IsLeaseLost(RequestFailedException ex) =>
        ex.Status == 404 || ex.Status == 400 && ex.ErrorCode == QueueErrorCode.PopReceiptMismatch.ToString();

    internal static bool IsQueueNotFound(RequestFailedException ex) => ex.Status == 404 && ex.ErrorCode == QueueErrorCode.QueueNotFound.ToString();

    internal async Task SendDeadLetterAsync(string deadLetterName, MessageSource source, string text, CancellationToken cancellationToken)
    {
        var message = new AzureStorageQueueEncodedMessage(text, TimeSpan.Zero);
        await SendToQueueAsync(deadLetterName, [message], () => DeadLetterMetadata(source), cancellationToken).AnyContext();
    }

    internal Task HeartbeatAsync(string queueName, string topic, CancellationToken cancellationToken) =>
        GetQueue(queueName).SetMetadataAsync(InstanceMetadata(topic), cancellationToken);

    internal async Task DeleteQueueAsync(string queueName)
    {
        _queues.TryRemove(queueName, out _);
        await GetQueue(queueName).DeleteIfExistsAsync().AnyContext();
        _queues.TryRemove(queueName, out _);
    }

    /// <summary>
    /// Removes the topic's instance subscription queues whose heartbeat lapsed. Runs when an instance receiver opens and
    /// on its heartbeats, at most once per <see cref="AzureStorageQueueMessageTransportOptions.InstanceSubscriptionTimeout"/>
    /// per topic and transport. Publishes skip and remove such queues as well.
    /// </summary>
    internal async Task SweepInstancesAsync(string topic)
    {
        long now = Stopwatch.GetTimestamp();
        long last = _lastSweeps.GetOrAdd(topic, 0);
        if (last != 0 && Stopwatch.GetElapsedTime(last, now) < Options.InstanceSubscriptionTimeout)
            return;
        if (!_lastSweeps.TryUpdate(topic, now, last))
            return;

        try
        {
            await GetSubscriptionQueuesAsync(topic, CancellationToken.None).AnyContext();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not remove stale instance subscriptions of topic {Topic}", topic);
        }
    }

    private AzureStorageQueueEncodedMessage Encode(OutgoingMessage message, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(message, nameof(message));
        long size = MessageSize.Of(message);
        if (size > AzureStorageQueueMessageTransportCodec.MaxMessageSize)
        {
            throw new MessageTooLargeException($"Message {message.MessageId} is {size} bytes with its headers and ids, which exceeds the Azure Storage Queues transport limit of {AzureStorageQueueMessageTransportCodec.MaxMessageSize} bytes. Store large payloads in file storage and send a reference.")
            {
                Size = size,
                Limit = AzureStorageQueueMessageTransportCodec.MaxMessageSize
            };
        }

        var (text, envelopeSize) = AzureStorageQueueMessageTransportCodec.Encode(message);
        if (envelopeSize > AzureStorageQueueMessageTransportCodec.MaxEnvelopeSize)
        {
            throw new MessageTooLargeException($"Message {message.MessageId} is {envelopeSize} bytes once encoded, over the {AzureStorageQueueMessageTransportCodec.MaxEnvelopeSize} bytes a Storage queue message holds: its {message.Headers.Count} headers need more framing than the transport reserves.")
            {
                Size = envelopeSize,
                Limit = AzureStorageQueueMessageTransportCodec.MaxEnvelopeSize
            };
        }

        var delay = message.DeliverAt is { } deliverAt ? ToVisibilityTimeout(deliverAt - now) : TimeSpan.Zero;
        return new AzureStorageQueueEncodedMessage(text, delay);
    }

    /// <summary>
    /// Sends the messages to the queue, as many at once as <see cref="MaxRequestsInFlight"/> allows, creating the queue
    /// with <paramref name="metadata"/> when Storage reports it missing (or with the subscription's when null).
    /// </summary>
    private async Task SendToQueueAsync(string queueName, IReadOnlyList<AzureStorageQueueEncodedMessage> messages, Func<IDictionary<string, string>>? metadata, CancellationToken cancellationToken)
    {
        var queue = GetQueue(queueName);
        Lazy<Task>? creating = null;
        async ValueTask SendOneAsync(AzureStorageQueueEncodedMessage message, CancellationToken token)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    await queue.SendMessageAsync(message.Text, message.Delay > TimeSpan.Zero ? message.Delay : null, TimeToLive, token).AnyContext();
                    return;
                }
                catch (RequestFailedException ex) when (IsQueueNotFound(ex) && attempt < 3 && metadata is not null)
                {
                    Logger.LogDebug("Queue {QueueName} does not exist; creating it", queueName);
                    var create = LazyInitializer.EnsureInitialized(ref creating, () => new Lazy<Task>(async () => await queue.CreateIfNotExistsAsync(metadata(), CancellationToken.None).AnyContext()));
                    await create.Value.WaitAsync(token).AnyContext();
                }
            }
        }

        if (messages.Count == 1)
            await SendOneAsync(messages[0], cancellationToken).ConfigureAwait(false);
        else
            await Parallel.ForEachAsync(messages, new ParallelOptions { MaxDegreeOfParallelism = MaxRequestsInFlight, CancellationToken = cancellationToken }, SendOneAsync).AnyContext();

        var shortest = TimeSpan.MaxValue;
        foreach (var message in messages)
        {
            if (message.Delay < shortest)
                shortest = message.Delay;
        }

        if (shortest == TimeSpan.Zero)
            Signal(queueName);
        else
            SignalAfter(queueName, shortest);
    }

    /// <summary>
    /// Creates the subscription's queue if needed and marks it as subscribed to its topic, so publishes reach it. Done
    /// on every receiver and every send to the subscription, not cached, so a subscription deleted by another process is restored.
    /// </summary>
    private async Task<string> SubscribeAsync(string topic, string subscription, CancellationToken cancellationToken)
    {
        string queueName = _names.Subscription(topic, subscription);
        var queue = GetQueue(queueName);
        var metadata = SubscriptionMetadata(topic, subscription);
        if (await queue.CreateIfNotExistsAsync(metadata, cancellationToken).AnyContext() is null)
            await queue.SetMetadataAsync(metadata, cancellationToken).AnyContext();

        return queueName;
    }

    /// <summary>
    /// Lists the queues subscribed to the topic, with one call (a page per 5,000 queues), skipping instance subscription
    /// queues whose heartbeat lapsed and removing them in the background.
    /// </summary>
    private async Task<IReadOnlyList<string>> GetSubscriptionQueuesAsync(string topic, CancellationToken cancellationToken)
    {
        var queues = new List<string>();
        List<string>? stale = null;
        long staleBefore = TimeProvider.GetUtcNow().Subtract(Options.InstanceSubscriptionTimeout).ToUnixTimeSeconds();
        await foreach (var item in Service.GetQueuesAsync(QueueTraits.Metadata, _names.TopicPrefix(topic), cancellationToken).ConfigureAwait(false))
        {
            if (item.Metadata is null || !TryGet(item.Metadata, TopicMetadata, out string? itemTopic) || !String.Equals(itemTopic, topic, StringComparison.Ordinal))
                continue;

            TryGet(item.Metadata, KindMetadata, out string? kind);
            if (kind == SubscriptionKind)
            {
                queues.Add(item.Name);
            }
            else if (kind == InstanceKind)
            {
                bool isStale = !TryGet(item.Metadata, HeartbeatMetadata, out string? heartbeat)
                    || !Int64.TryParse(heartbeat, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds) || seconds < staleBefore;
                if (isStale)
                    (stale ??= []).Add(item.Name);
                else
                    queues.Add(item.Name);
            }
        }

        if (stale is not null)
            _ = RemoveStaleInstancesAsync(topic, stale);

        return queues;
    }

    private async Task RemoveStaleInstancesAsync(string topic, IReadOnlyList<string> queueNames)
    {
        try
        {
            foreach (string queueName in queueNames)
                await DeleteQueueAsync(queueName).AnyContext();

            Logger.LogInformation("Removed {Count} stale instance subscriptions of topic {Topic}", queueNames.Count, topic);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not remove stale instance subscriptions of topic {Topic}", topic);
        }
    }

    private async Task<long> GetCountAsync(string queueName, CancellationToken cancellationToken)
    {
        try
        {
            var properties = await GetQueue(queueName).GetPropertiesAsync(cancellationToken).AnyContext();
            return properties.Value.ApproximateMessagesCount;
        }
        catch (RequestFailedException ex) when (IsQueueNotFound(ex))
        {
            return 0;
        }
    }

    /// <summary>
    /// Clears the queue, again while Storage reports that it timed out, as it does for queues with many messages.
    /// Succeeds when the queue does not exist.
    /// </summary>
    private async Task ClearAsync(string queueName, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await GetQueue(queueName).ClearMessagesAsync(cancellationToken).AnyContext();
                return;
            }
            catch (RequestFailedException ex) when (IsQueueNotFound(ex))
            {
                return;
            }
            catch (RequestFailedException ex) when (ex.ErrorCode == "OperationTimedOut" && attempt < MaxClearAttempts)
            {
                Logger.LogDebug("Clearing queue {QueueName} timed out; clearing the rest", queueName);
            }
        }
    }

    private static bool TryGet(IDictionary<string, string> metadata, string key, out string? value)
    {
        if (metadata.TryGetValue(key, out value))
            return true;

        foreach (var (name, item) in metadata)
        {
            if (String.Equals(name, key, StringComparison.OrdinalIgnoreCase))
            {
                value = item;
                return true;
            }
        }

        return false;
    }

    private static Dictionary<string, string> QueueMetadata(string kind, string name) => new()
    {
        [KindMetadata] = kind,
        [NameMetadata] = name
    };

    private static Dictionary<string, string> SubscriptionMetadata(string topic, string subscription) => new()
    {
        [KindMetadata] = SubscriptionKind,
        [TopicMetadata] = topic,
        [NameMetadata] = subscription
    };

    private Dictionary<string, string> InstanceMetadata(string topic) => new()
    {
        [KindMetadata] = InstanceKind,
        [TopicMetadata] = topic,
        [HeartbeatMetadata] = TimeProvider.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
    };

    private static Dictionary<string, string> DeadLetterMetadata(MessageSource source) => new()
    {
        [KindMetadata] = DeadLetterKind,
        [NameMetadata] = source.ToString()
    };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);

    private static QueueServiceClient CreateService(AzureStorageQueueMessageTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var clientOptions = new QueueClientOptions();
        options.ConfigureClient?.Invoke(clientOptions);
        clientOptions.MessageEncoding = QueueMessageEncoding.None;

        if (!String.IsNullOrEmpty(options.ConnectionString))
            return new QueueServiceClient(options.ConnectionString, clientOptions);

        if (options.ServiceUri is null)
            throw new ArgumentException("Set ConnectionString, or ServiceUri with a Credential, in the Azure Storage Queues transport options.", nameof(options));

        return options.Credential is null
            ? new QueueServiceClient(options.ServiceUri, clientOptions)
            : new QueueServiceClient(options.ServiceUri, options.Credential, clientOptions);
    }
}

/// <summary>A message's text, and how long it stays invisible after it is sent.</summary>
internal sealed record AzureStorageQueueEncodedMessage(string Text, TimeSpan Delay);

/// <summary>Wakes the receives waiting on one queue of this process: each <see cref="Set"/> completes the tasks handed out before it.</summary>
internal sealed class AzureStorageQueueSignal
{
    private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitAsync() => Volatile.Read(ref _next).Task;

    public void Set() => Interlocked.Exchange(ref _next, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
}

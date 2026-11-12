using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Foundatio.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// A message transport on Kafka. A queue is a topic read by one consumer group; a topic is a Kafka topic with one
/// consumer group per durable subscription; an instance subscription reads every partition of the topic directly,
/// from the end, without a consumer group, so nothing is left behind when its process stops.
/// </summary>
/// <remarks>
/// <para>
/// Offsets are committed up to the lowest unfinished message of each partition, so messages are handled concurrently
/// and settled in any order. Leases do not expire: an unsettled message stays with its receiver until it is settled,
/// the receiver is disposed, or a rebalance moves its partition.
/// </para>
/// <para>
/// Abandoned and released messages without a group key are copied, with an incremented delivery count, to a topic
/// only their source reads (the queue itself, or the subscription's own topic), and their offset is finished.
/// Messages with a group key are released in place, so their group stays in order. Dead letters go to a topic per
/// source. Delays go through shared delay topics that a forwarder, running in every process with a receiver, moves
/// on when they are due.
/// </para>
/// </remarks>
public sealed class KafkaMessageTransport : IMessageTransport
{
    private static readonly TimeSpan AdminTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TopicReadyTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ForwarderRestartDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxForwarderRestartDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The shortest retention of a delay tier topic. A record moves on within six hours while forwarders run; the rest
    /// covers time without any.
    /// </summary>
    internal static readonly TimeSpan DelayRetention = TimeSpan.FromDays(7);

    /// <summary>The shortest retention of a dead-letter topic, the longest SQS keeps a message.</summary>
    internal static readonly TimeSpan DeadLetterRetention = TimeSpan.FromDays(14);

    /// <summary>How long a receiver waits for a partition's state before handing out its records without it.</summary>
    private static readonly TimeSpan StateLoadTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long after the state topic could not be read a receiver tries again.</summary>
    private static readonly TimeSpan StateStoreRetryDelay = TimeSpan.FromMinutes(1);

    private readonly IAdminClient _admin;
    private readonly ConcurrentDictionary<string, Lazy<Task>> _topics = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<KafkaMessageReceiver, byte> _receivers = new();
    private readonly object _forwarderLock = new();
    private Task<KafkaDelayForwarder>? _forwarder;
    private readonly object _stateStoreLock = new();
    private Task<KafkaReceiverStateStore?>? _stateStore;
    private long _stateStoreFailedAt;
    private int _disposed;

    /// <summary>Creates a transport with its own producer and admin client. Receivers each get their own consumer.</summary>
    public KafkaMessageTransport(KafkaMessageTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.BootstrapServers, nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Prefix, nameof(options));
        if (options.Prefix.Any(c => !(Char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')))
            throw new ArgumentException($"The prefix \"{options.Prefix}\" may only contain letters, digits, '.', '_' and '-'.", nameof(options));
        if (options.Partitions is { } partitions)
            ArgumentOutOfRangeException.ThrowIfLessThan(partitions, 1, nameof(options));
        if (options.ReplicationFactor is { } replicationFactor)
            ArgumentOutOfRangeException.ThrowIfLessThan(replicationFactor, (short)1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxMessageBytes, KafkaMessageTransportCodec.EncodingReserve * 2, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.CommitInterval, TimeSpan.Zero, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.RebalanceGracePeriod, TimeSpan.Zero, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.ReceiverJoinTimeout, TimeSpan.Zero, nameof(options));

        Options = options;
        Names = new KafkaMessageTransportNames(options.Prefix);
        TimeProvider = options.TimeProvider ?? TimeProvider.System;
        Logger = (options.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger<KafkaMessageTransport>();
        var maxDelay = options.DelayedDelivery ? (TimeSpan?)null : TimeSpan.Zero;
        Capabilities = new TransportCapabilities
        {
            MaxDelay = maxDelay,
            MaxPublishDelay = maxDelay,
            MaxRetryDelay = maxDelay,
            MinRetryDelay = options.DelayedDelivery ? KafkaDelayTiers.Smallest : TimeSpan.Zero,
            MaxSendBatchSize = 1000,
            MaxReceiveBatchSize = 100,
            MaxMessageSize = options.MaxMessageBytes - KafkaMessageTransportCodec.EncodingReserve,
            MaxLeaseDuration = null,
            LeaseExpiration = false,
            OrderedGroups = true,
            Deduplication = false
        };

        Producer = CreateProducer();
        _admin = new DependentAdminClientBuilder(Producer.Handle).Build();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Send, publish and retry delays are unlimited when <see cref="KafkaMessageTransportOptions.DelayedDelivery"/>
    /// is on, and unsupported when it is off. Retries shorter than the shortest delay tier (one second) are held by
    /// Foundatio in-process. Leases never expire; renewing reports a delivery whose partition was revoked. Groups are ordered: a group maps to one partition,
    /// a receive returns a group's messages in order, and none is handed out while an earlier one is unsettled.
    /// There is no deduplication.
    /// </remarks>
    public TransportCapabilities Capabilities { get; }

    internal KafkaMessageTransportOptions Options { get; }
    internal KafkaMessageTransportNames Names { get; }
    internal TimeProvider TimeProvider { get; }
    internal ILogger Logger { get; }
    internal IProducer<byte[]?, byte[]> Producer { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Topics are created on first use. Delayed messages without a group key go to a delay topic and reach the
    /// destination when due; delayed messages with one go to the destination at once, and receivers hold them (and the
    /// group's later messages) until they are due. A message to one subscription goes to that subscription's own
    /// topic, which only it reads.
    /// </remarks>
    /// <exception cref="MessageTooLargeException">A message is larger than <see cref="TransportCapabilities.MaxMessageSize"/>.</exception>
    public async Task SendAsync(MessageDestination destination, IReadOnlyList<OutgoingMessage> messages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(messages);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (messages.Count == 0)
            return;

        string topic = destination.Kind switch
        {
            MessageDestinationKind.Queue => Names.Queue(destination.Name),
            MessageDestinationKind.Topic => Names.Topic(destination.Name),
            MessageDestinationKind.Subscription => Names.SubscriptionInbox(destination.Name, destination.SubscriptionName!),
            _ => throw new ArgumentOutOfRangeException(nameof(destination), destination.Kind, "Unknown destination kind.")
        };

        long maxSize = Capabilities.MaxMessageSize!.Value;
        long maxRecordSize = Options.MaxMessageBytes - KafkaMessageTransportCodec.EncodingReserve / 2;
        var now = TimeProvider.GetUtcNow();
        var records = new List<(string Topic, Message<byte[]?, byte[]> Message)>(messages.Count);
        bool delayed = false;

        foreach (var message in messages)
        {
            ArgumentNullException.ThrowIfNull(message, nameof(messages));
            KafkaMessageTransportCodec.ThrowIfReserved(message.Headers);

            long size = MessageSize.Of(message);
            if (size > maxSize)
                throw new MessageTooLargeException($"Message {message.MessageId} is {size} bytes with headers and ids, which exceeds the transport limit of {maxSize} bytes.") { Size = size, Limit = maxSize };

            // The reserve covers the transport's headers and framing; only thousands of tiny headers could exceed it.
            var record = KafkaMessageTransportCodec.Encode(message.MessageId, message.Body, message.Headers, message.GroupKey);
            long encoded = KafkaMessageTransportCodec.GetSize(record);
            if (encoded > maxRecordSize)
                throw new MessageTooLargeException($"Message {message.MessageId} encodes to {encoded} bytes, which exceeds the record limit of {maxRecordSize} bytes.") { Size = encoded, Limit = maxRecordSize };

            if (message.DeliverAt is { } deliverAt && deliverAt > now)
            {
                if (!Options.DelayedDelivery)
                    throw new ArgumentException("Delayed delivery is turned off for this transport.", nameof(messages));

                // A delayed message holds back its group's later ones, so it goes to its group's partition now and
                // the receiver keeps it until it is due. Without a group it waits in the delay topics instead.
                if (message.GroupKey is not null)
                {
                    records.Add((topic, KafkaMessageTransportCodec.WithDeliverAt(record, deliverAt)));
                    continue;
                }

                records.Add((Names.Delay(KafkaDelayTiers.Choose(deliverAt - now)), KafkaMessageTransportCodec.WithDelay(record, topic, deliverAt)));
                delayed = true;
                continue;
            }

            records.Add((topic, record));
        }

        await EnsureTopicsAsync([topic]).AnyContext();
        if (delayed)
            await EnsureForwarderAsync(cancellationToken).AnyContext();

        await ProduceAsync(records, cancellationToken).AnyContext();
    }

    /// <inheritdoc />
    /// <remarks>
    /// A queue or subscription receiver is returned once it has joined its consumer group, waiting at most
    /// <see cref="KafkaMessageTransportOptions.ReceiverJoinTimeout"/>; nothing sent after this returns is missed. An
    /// instance subscription starts at the current end of its topic.
    /// </remarks>
    public async Task<IMessageReceiver> CreateReceiverAsync(MessageSource source, ReceiverOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfDisposed();

        var topology = await ResolveAsync(source, cancellationToken).AnyContext();
        if (!topology.IsInstance)
            await EnsureForwarderAsync(cancellationToken).AnyContext();

        var receiver = new KafkaMessageReceiver(this, topology, options);
        _receivers.TryAdd(receiver, 0);
        if (Volatile.Read(ref _disposed) == 1)
        {
            await receiver.DisposeAsync().ConfigureAwait(false);
            ThrowIfDisposed();
        }

        receiver.Start();
        try
        {
            if (!topology.IsInstance && Options.ReceiverJoinTimeout > TimeSpan.Zero && !await receiver.WaitUntilJoinedAsync(Options.ReceiverJoinTimeout, cancellationToken).AnyContext())
                Logger.LogDebug("{Source} has not joined its consumer group after {Timeout}; it keeps joining in the background", source, Options.ReceiverJoinTimeout);
        }
        catch
        {
            // Nobody holds the receiver, so it would keep its partitions without handing them out.
            await receiver.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return receiver;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Deletes the source's consumer group and the topics only it uses: a queue's topic, a subscription's own topic
    /// (not the shared topic it subscribes to), and the dead-letter topic. Deleting a subscription's group drops its
    /// position on the topic, so receiving again starts at the end of the topic. Delayed messages for the source that
    /// are still in the shared delay topics are dropped when they come due, unless the source was created again.
    /// Throws <see cref="MessageBusException"/> when a receiver, in any process, is still open on the source.
    /// </remarks>
    public async Task DeleteAsync(MessageSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfDisposed();

        List<string> topics = source.Kind switch
        {
            MessageSourceKind.Queue => [Names.Queue(source.Name), Names.DeadLetter(source)],
            MessageSourceKind.Subscription => [Names.SubscriptionInbox(source.Topic!, source.Name), Names.DeadLetter(source)],
            MessageSourceKind.DeadLetter => [Names.DeadLetter(source)],
            _ => throw new ArgumentException("Instance subscriptions are removed by disposing their receiver.", nameof(source))
        };

        // Every source's consumer group is named after its own topic, so the topics name the groups too.
        await DeleteGroupsAsync(topics, source).AnyContext();
        foreach (string topic in topics)
            _topics.TryRemove(topic, out _);

        try
        {
            await _admin.DeleteTopicsAsync(topics, new DeleteTopicsOptions { RequestTimeout = AdminTimeout }).AnyContext();
        }
        catch (DeleteTopicsException ex) when (ex.Results.All(r => r.Error.Code is ErrorCode.NoError or ErrorCode.UnknownTopicOrPart))
        {
        }

        await WaitForTopicsDeletedAsync(topics, cancellationToken).AnyContext();
        Logger.LogInformation("Deleted {Source}", source);
    }

    /// <summary>Closes every receiver (their unsettled messages are released), stops the delay forwarder and flushes the producer.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        await Task.WhenAll(_receivers.Keys.Select(r => r.DisposeAsync().AsTask())).AnyContext();

        Task<KafkaReceiverStateStore?>? stateStore;
        lock (_stateStoreLock)
            stateStore = _stateStore;

        if (stateStore is { IsCompletedSuccessfully: true, Result: { } store })
            await store.DisposeAsync().ConfigureAwait(false);

        Task<KafkaDelayForwarder>? forwarder;
        lock (_forwarderLock)
            forwarder = _forwarder;

        if (forwarder is not null)
        {
            try
            {
                await (await forwarder.AnyContext()).DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Delay forwarder did not start");
            }
        }

        await Task.Run(() => Producer.Flush(FlushTimeout)).AnyContext();
        _admin.Dispose();
        Producer.Dispose();
    }

    internal ConsumerConfig CreateConsumerConfig(string? groupId)
    {
        var config = new ConsumerConfig(CopyClientConfig())
        {
            BootstrapServers = Options.BootstrapServers,
            GroupId = groupId ?? $"{Options.Prefix}.instance.{Guid.NewGuid():N}",
            EnableAutoCommit = groupId is not null,
            EnableAutoOffsetStore = false,
            AutoCommitIntervalMs = (int)Math.Max(1, Options.CommitInterval.TotalMilliseconds),
            AutoOffsetReset = AutoOffsetReset.Earliest,
            PartitionAssignmentStrategy = PartitionAssignmentStrategy.CooperativeSticky,
            AllowAutoCreateTopics = false,
            EnablePartitionEof = false,
            // Receivers buffer what they hand out themselves; librdkafka's prefetch only needs to keep them fed.
            QueuedMaxMessagesKbytes = 16 * 1024
        };

        Options.ConfigureConsumer?.Invoke(config);
        if (config.GroupProtocol == GroupProtocol.Consumer)
            config.PartitionAssignmentStrategy = null;

        return config;
    }

    internal async Task RepublishAsync(KafkaSourceTopology topology, IncomingMessage message, TimeSpan delay, CancellationToken cancellationToken)
    {
        string target = topology.RetryTopic ?? throw new InvalidOperationException($"{topology.Source} has no retry topic.");
        var record = KafkaMessageTransportCodec.Encode(message.MessageId, message.Body, message.Headers, message.GroupKey, message.DeliveryCount + 1, message.EnqueuedAt);
        if (delay <= TimeSpan.Zero || !Options.DelayedDelivery)
        {
            await ProduceAsync([(target, record)], cancellationToken).AnyContext();
            return;
        }

        await EnsureForwarderAsync(cancellationToken).AnyContext();
        var deliverAt = TimeProvider.GetUtcNow().Add(delay);
        await ProduceAsync([(Names.Delay(KafkaDelayTiers.Choose(delay)), KafkaMessageTransportCodec.WithDelay(record, target, deliverAt))], cancellationToken).AnyContext();
    }

    internal async Task DeadLetterAsync(KafkaSourceTopology topology, IncomingMessage message, string reason, CancellationToken cancellationToken)
    {
        string topic = topology.DeadLetterTopic ?? throw new InvalidOperationException($"{topology.Source} has no dead-letter queue.");
        await EnsureTopicsAsync([topic]).AnyContext();

        if (reason.Length > KafkaMessageTransportCodec.MaxReasonLength)
            reason = reason[..KafkaMessageTransportCodec.MaxReasonLength];

        var headers = new Dictionary<string, string>(message.Headers) { [KnownHeaders.DeadLetterReason] = reason };
        var record = KafkaMessageTransportCodec.Encode(message.MessageId, message.Body, headers, message.GroupKey, enqueuedAt: message.EnqueuedAt);
        await ProduceAsync([(topic, record)], cancellationToken).AnyContext();
    }

    internal void Remove(KafkaMessageReceiver receiver) => _receivers.TryRemove(receiver, out _);

    /// <summary>
    /// The state a receiver kept for <paramref name="key"/> in the state topic, or <c>null</c> when there is none, or
    /// when the state topic cannot be read within <see cref="StateLoadTimeout"/>.
    /// </summary>
    internal async Task<KafkaReceiverState?> LoadReceiverStateAsync(string key, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StateLoadTimeout);
        try
        {
            if (await GetStateStoreAsync().WaitAsync(timeout.Token).AnyContext() is not { } store)
                return null;

            return await store.LoadAsync(key, timeout.Token).AnyContext() is { } value ? KafkaReceiverState.Decode(value) : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Logger.LogWarning("Could not read the receiver state for {Key} within {Timeout}; delivering without it", key.Replace('\n', '/'), StateLoadTimeout);
            return null;
        }
    }

    /// <summary>Writes a receiver's state for <paramref name="key"/> to the state topic; an empty state deletes it.</summary>
    internal async Task WriteReceiverStateAsync(string key, KafkaReceiverState state)
    {
        if (await GetStateStoreAsync().AnyContext() is { } store)
            await store.WriteAsync(key, state.IsEmpty ? null : state.Encode()).AnyContext();
    }

    private Task<KafkaReceiverStateStore?> GetStateStoreAsync()
    {
        lock (_stateStoreLock)
        {
            bool retry = _stateStore is { IsCompletedSuccessfully: true, Result: null } && Stopwatch.GetElapsedTime(_stateStoreFailedAt) > StateStoreRetryDelay;
            if (_stateStore is null || retry)
                _stateStore = StartStateStoreAsync();

            return _stateStore;
        }
    }

    private async Task<KafkaReceiverStateStore?> StartStateStoreAsync()
    {
        string topic = Names.ReceiverState;
        try
        {
            if (!Options.CreateTopics)
            {
                var metadata = await Task.Run(() => _admin.GetMetadata(topic, AdminTimeout)).AnyContext();
                if (metadata.Topics.FirstOrDefault(t => t.Topic == topic) is not { Error.IsError: false })
                    throw new MessageBusException($"Topic {topic} does not exist.");
            }

            await EnsureTopicsAsync([topic]).AnyContext();
            var partitions = await GetPartitionsAsync(topic, CancellationToken.None).AnyContext();
            ThrowIfDisposed();
            var store = new KafkaReceiverStateStore(this, topic, partitions);
            store.Start();
            return store;
        }
        catch (Exception ex)
        {
            _stateStoreFailedAt = Stopwatch.GetTimestamp();
            Logger.LogWarning(ex, "Cannot use the receiver state topic {Topic}: delivery counts of grouped retries restart when a partition moves, and messages finished behind an unfinished one are delivered again", topic);
            return null;
        }
    }

    /// <summary>How many receivers are open. For tests.</summary>
    internal int ReceiverCount => _receivers.Count;

    /// <summary>Starts the delay forwarder if needed and returns it. For tests.</summary>
    internal async Task<KafkaDelayForwarder> GetForwarderAsync()
    {
        await EnsureForwarderAsync(CancellationToken.None).AnyContext();
        Task<KafkaDelayForwarder> forwarder;
        lock (_forwarderLock)
            forwarder = _forwarder!;

        return await forwarder.AnyContext();
    }

    internal void OnLog(LogMessage message)
    {
        var level = message.Level switch
        {
            SyslogLevel.Emergency or SyslogLevel.Alert or SyslogLevel.Critical or SyslogLevel.Error => LogLevel.Error,
            SyslogLevel.Warning => LogLevel.Warning,
            SyslogLevel.Notice or SyslogLevel.Info => LogLevel.Debug,
            _ => LogLevel.Trace
        };

        if (Logger.IsEnabled(level))
            Logger.Log(level, "Kafka {Facility} ({Client}): {Message}", message.Facility, message.Name, message.Message);
    }

    internal void OnError(Error error)
    {
        if (error.IsFatal)
            Logger.LogError("Kafka fatal error {Code}: {Reason}", error.Code, error.Reason);
        else
            Logger.LogWarning("Kafka error {Code}: {Reason}", error.Code, error.Reason);
    }

    private async Task DeleteGroupsAsync(IReadOnlyList<string> groups, MessageSource source)
    {
        try
        {
            await _admin.DeleteGroupsAsync(groups.ToList(), new DeleteGroupsOptions { RequestTimeout = AdminTimeout }).AnyContext();
        }
        catch (DeleteGroupsException ex)
        {
            var active = ex.Results.Where(r => r.Error.Code == ErrorCode.NonEmptyGroup).Select(r => r.Group).ToList();
            if (active.Count > 0)
                throw new MessageBusException($"Cannot delete {source} while receivers are open on it (consumer groups {String.Join(", ", active)}).", ex);

            var failed = ex.Results.FirstOrDefault(r => r.Error.Code is not (ErrorCode.NoError or ErrorCode.GroupIdNotFound));
            if (failed is not null)
                throw new MessageBusException($"Could not delete consumer group {failed.Group} of {source}: {failed.Error.Reason}", ex);
        }
    }

    private async Task WaitForTopicsDeletedAsync(IReadOnlyList<string> topics, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        foreach (string topic in topics)
        {
            while (true)
            {
                var metadata = await Task.Run(() => _admin.GetMetadata(topic, AdminTimeout), cancellationToken).AnyContext();
                if (metadata.Topics.FirstOrDefault(t => t.Topic == topic) is not { Error.IsError: false })
                    break;

                if (Stopwatch.GetElapsedTime(started) > TopicReadyTimeout)
                    throw new MessageBusException($"Topic {topic} was not deleted in time.");

                await Task.Delay(100, cancellationToken).AnyContext();
            }
        }
    }

    private async Task<KafkaSourceTopology> ResolveAsync(MessageSource source, CancellationToken cancellationToken)
    {
        switch (source.Kind)
        {
            case MessageSourceKind.Queue:
                {
                    string queue = Names.Queue(source.Name);
                    await EnsureTopicsAsync([queue]).AnyContext();
                    return new KafkaSourceTopology { Source = source, Topics = [queue], GroupId = queue, RetryTopic = queue, DeadLetterTopic = Names.DeadLetter(source) };
                }
            case MessageSourceKind.Subscription:
                {
                    string topic = Names.Topic(source.Topic!);
                    string inbox = Names.SubscriptionInbox(source.Topic!, source.Name);
                    await EnsureTopicsAsync([topic, inbox]).AnyContext();
                    await InitializeSubscriptionAsync(inbox, topic, cancellationToken).AnyContext();
                    return new KafkaSourceTopology { Source = source, Topics = [topic, inbox], GroupId = inbox, RetryTopic = inbox, DeadLetterTopic = Names.DeadLetter(source) };
                }
            case MessageSourceKind.DeadLetter:
                {
                    string deadLetters = Names.DeadLetter(source);
                    await EnsureTopicsAsync([deadLetters]).AnyContext();
                    return new KafkaSourceTopology { Source = source, Topics = [deadLetters], GroupId = deadLetters, RetryTopic = deadLetters, DeadLetterTopic = deadLetters };
                }
            case MessageSourceKind.InstanceSubscription:
                {
                    string topic = Names.Topic(source.Topic!);
                    await EnsureTopicsAsync([topic]).AnyContext();
                    var ends = await GetEndOffsetsAsync(await GetPartitionsAsync(topic, cancellationToken).AnyContext(), cancellationToken).AnyContext();
                    return new KafkaSourceTopology { Source = source, Topics = [topic], Assignment = ends };
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(source), source.Kind, "Unknown source kind.");
        }
    }

    /// <summary>
    /// A new subscription starts at the end of its topic. Its offsets are committed now, before any receiver joins, so
    /// messages published while no receiver is open are kept even if the first receiver closes without reading. A
    /// group with no committed offsets at all is treated as new, including one whose offsets the broker expired
    /// (<c>offsets.retention.minutes</c>) while it had no members.
    /// </summary>
    private async Task InitializeSubscriptionAsync(string groupId, string topic, CancellationToken cancellationToken)
    {
        var partitions = await GetPartitionsAsync(topic, cancellationToken).AnyContext();
        var missing = await GetUncommittedPartitionsAsync(groupId, partitions, cancellationToken).AnyContext();

        // Partitions added to an existing subscription's topic are read from the start (auto.offset.reset).
        if (missing.Count == 0 || missing.Count < partitions.Count)
            return;

        var ends = await GetEndOffsetsAsync(missing, cancellationToken).AnyContext();
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                await _admin.AlterConsumerGroupOffsetsAsync([new ConsumerGroupTopicPartitionOffsets(groupId, ends)],
                    new AlterConsumerGroupOffsetsOptions { RequestTimeout = AdminTimeout }).AnyContext();
                Logger.LogDebug("Started subscription group {GroupId} at the end of {Topic}", groupId, topic);
                return;
            }
            catch (AlterConsumerGroupOffsetsException ex) when (!IsCoordinatorUnavailable(ex))
            {
                // A receiver in another process joined first; the group already has a position.
                Logger.LogDebug(ex, "Could not set the starting offsets of subscription group {GroupId}", groupId);
                return;
            }
            catch (AlterConsumerGroupOffsetsException ex) when (Stopwatch.GetElapsedTime(started) < TopicReadyTimeout)
            {
                Logger.LogDebug(ex, "Retrying setting the starting offsets of subscription group {GroupId}", groupId);
                await Task.Delay(100, cancellationToken).AnyContext();
            }
        }
    }

    /// <summary>
    /// The partitions a group has no committed offset for, read through a consumer of the group. The admin client's
    /// ListConsumerGroupOffsets is not used: on a client without a consumer group, librdkafka crashes the process when
    /// the group coordinator answers with a retriable error, as a broker that is still starting does.
    /// </summary>
    private Task<List<TopicPartition>> GetUncommittedPartitionsAsync(string groupId, List<TopicPartition> partitions, CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            using var consumer = new ConsumerBuilder<byte[]?, byte[]?>(CreateConsumerConfig(groupId))
                .SetLogHandler((_, message) => OnLog(message))
                .SetErrorHandler((_, error) => OnError(error))
                .Build();

            long started = Stopwatch.GetTimestamp();
            while (true)
            {
                try
                {
                    return consumer.Committed(partitions, AdminTimeout).Where(p => p.Offset == Offset.Unset).Select(p => p.TopicPartition).ToList();
                }
                catch (KafkaException ex) when (IsCoordinatorUnavailable(ex.Error) && Stopwatch.GetElapsedTime(started) < TopicReadyTimeout)
                {
                    Logger.LogDebug(ex, "Retrying reading the committed offsets of group {GroupId}", groupId);
                    await Task.Delay(100, cancellationToken).AnyContext();
                }
            }
        }, cancellationToken);
    }

    private static bool IsCoordinatorUnavailable(AlterConsumerGroupOffsetsException ex)
    {
        return IsCoordinatorUnavailable(ex.Error) || ex.Results.Any(r => IsCoordinatorUnavailable(r.Error) || r.Partitions.Any(p => IsCoordinatorUnavailable(p.Error)));
    }

    /// <summary>The errors of a group coordinator that has moved or is still loading, as on a broker that is starting.</summary>
    private static bool IsCoordinatorUnavailable(Error error)
    {
        return error.Code is ErrorCode.GroupLoadInProgress or ErrorCode.GroupCoordinatorNotAvailable or ErrorCode.NotCoordinatorForGroup or ErrorCode.RequestTimedOut;
    }

    private Task EnsureForwarderAsync(CancellationToken cancellationToken)
    {
        if (!Options.DelayedDelivery)
            return Task.CompletedTask;

        Task<KafkaDelayForwarder> forwarder;
        lock (_forwarderLock)
        {
            ThrowIfDisposed();
            if (_forwarder is null || _forwarder.IsFaulted || _forwarder.IsCanceled || _forwarder.IsCompletedSuccessfully && _forwarder.Result.Failed)
                _forwarder = StartForwarderAsync();

            forwarder = _forwarder;
        }

        return forwarder.WaitAsync(cancellationToken);
    }

    private async Task<KafkaDelayForwarder> StartForwarderAsync()
    {
        var tiers = KafkaDelayTiers.All.ToDictionary(Names.Delay, t => t);
        await EnsureTopicsAsync(tiers.Keys.ToList()).AnyContext();
        var forwarder = new KafkaDelayForwarder(this, tiers);
        forwarder.Start();
        _ = RestartWhenFailedAsync(forwarder);
        return forwarder;
    }

    /// <summary>
    /// Delayed sends and retries stop moving in this process while its forwarder is down, and nothing else would start
    /// it again until the next delayed send, so a forwarder that fails is restarted, backing off while it keeps failing.
    /// </summary>
    private async Task RestartWhenFailedAsync(KafkaDelayForwarder forwarder)
    {
        await forwarder.Completion.AnyContext();
        var delay = ForwarderRestartDelay;
        while (forwarder.Failed && Volatile.Read(ref _disposed) == 0)
        {
            Logger.LogError("The Kafka delay forwarder failed; delayed messages wait until it restarts in {Delay}", delay);
            await Task.Delay(delay).AnyContext();
            try
            {
                await EnsureForwarderAsync(CancellationToken.None).AnyContext();
                Logger.LogInformation("Restarted the Kafka delay forwarder");
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Could not restart the Kafka delay forwarder: {Message}", ex.Message);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxForwarderRestartDelay.Ticks));
            }
        }
    }

    private Task ProduceAsync(IReadOnlyList<(string Topic, Message<byte[]?, byte[]> Message)> records, CancellationToken cancellationToken)
    {
        return Task.WhenAll(records.Select(r => ProduceAsync(r.Topic, r.Message, cancellationToken)));
    }

    private async Task ProduceAsync(string topic, Message<byte[]?, byte[]> message, CancellationToken cancellationToken)
    {
        try
        {
            await ProduceOnceAsync(topic, message, cancellationToken).AnyContext();
        }
        catch (ProduceException<byte[]?, byte[]> ex) when (Options.CreateTopics && ex.Error.Code is ErrorCode.UnknownTopicOrPart or ErrorCode.Local_UnknownTopic)
        {
            // Deleted since this process created it (DeleteAsync elsewhere): create it again, then send once more.
            Logger.LogDebug("Topic {Topic} no longer exists; creating it again", topic);
            _topics.TryRemove(topic, out _);
            await EnsureTopicsAsync([topic]).AnyContext();
            try
            {
                await ProduceOnceAsync(topic, message, cancellationToken).AnyContext();
            }
            catch (ProduceException<byte[]?, byte[]> retryEx)
            {
                throw ToException(topic, retryEx);
            }
        }
        catch (ProduceException<byte[]?, byte[]> ex)
        {
            throw ToException(topic, ex);
        }
    }

    private async Task ProduceOnceAsync(string topic, Message<byte[]?, byte[]> message, CancellationToken cancellationToken)
    {
        var result = await Producer.ProduceAsync(topic, message, cancellationToken).AnyContext();
        if (result.Status == PersistenceStatus.NotPersisted)
            throw new MessageBusException($"Message was not persisted to {result.TopicPartitionOffset}.");
    }

    private static MessageBusException ToException(string topic, ProduceException<byte[]?, byte[]> ex)
    {
        return ex.Error.Code is ErrorCode.MsgSizeTooLarge or ErrorCode.InvalidMsgSize
            ? new MessageTooLargeException($"Kafka rejected a message to {topic} as too large: {ex.Error.Reason}", ex)
            : new MessageBusException($"Could not send to {topic}: {ex.Error.Reason}", ex);
    }

    private async Task EnsureTopicsAsync(IReadOnlyList<string> names)
    {
        if (!Options.CreateTopics)
            return;

        await Task.WhenAll(names.Select(EnsureTopicAsync)).AnyContext();
    }

    private async Task EnsureTopicAsync(string name)
    {
        var creation = _topics.GetOrAdd(name, n => new Lazy<Task>(() => CreateTopicAsync(n)));
        try
        {
            await creation.Value.AnyContext();
        }
        catch
        {
            _topics.TryRemove(new KeyValuePair<string, Lazy<Task>>(name, creation));
            throw;
        }
    }

    private async Task CreateTopicAsync(string name)
    {
        var configs = GetTopicConfigs(name);
        var specification = new TopicSpecification
        {
            Name = name,
            NumPartitions = Options.Partitions ?? -1,
            ReplicationFactor = Options.ReplicationFactor ?? -1,
            Configs = configs
        };

        try
        {
            await _admin.CreateTopicsAsync([specification], new CreateTopicsOptions { RequestTimeout = AdminTimeout }).AnyContext();
            Logger.LogDebug("Created topic {Topic}", name);
        }
        catch (CreateTopicsException ex) when (ex.Results.All(r => r.Error.Code is ErrorCode.NoError or ErrorCode.TopicAlreadyExists))
        {
        }

        await GetPartitionsAsync(name, CancellationToken.None).AnyContext();
    }

    /// <summary>
    /// <see cref="KafkaMessageTransportOptions.TopicConfigs"/>, except where they would lose messages the transport
    /// keeps for itself: delay tiers and dead-letter topics are never compacted (their records have no key) and keep
    /// their records at least <see cref="DelayRetention"/> and <see cref="DeadLetterRetention"/>, whatever their size;
    /// delay tiers also need the broker's append time to know when a record is due.
    /// </summary>
    private Dictionary<string, string> GetTopicConfigs(string topic)
    {
        var configs = Options.TopicConfigs is { } topicConfigs ? new Dictionary<string, string>(topicConfigs) : new Dictionary<string, string>();
        if (topic == Names.ReceiverState)
        {
            // Only the latest state of each key matters; small segments let the log cleaner drop the rest soon.
            configs["cleanup.policy"] = "compact";
            configs["segment.ms"] = ((long)TimeSpan.FromHours(1).TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
            configs["min.cleanable.dirty.ratio"] = "0.1";
            return configs;
        }

        TimeSpan? minimumRetention = Names.IsDelay(topic) ? DelayRetention : Names.IsDeadLetter(topic) ? DeadLetterRetention : null;
        if (minimumRetention is not { } minimum)
            return configs;

        configs["cleanup.policy"] = "delete";
        configs["retention.bytes"] = "-1";
        if (!(configs.TryGetValue("retention.ms", out string? retention) && Int64.TryParse(retention, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long milliseconds)
            && (milliseconds < 0 || milliseconds >= minimum.TotalMilliseconds)))
        {
            configs["retention.ms"] = ((long)minimum.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
        }

        if (Names.IsDelay(topic))
            configs["message.timestamp.type"] = "LogAppendTime";

        return configs;
    }

    /// <summary>The partitions of a topic, waiting until a newly created topic has leaders for all of them.</summary>
    private async Task<List<TopicPartition>> GetPartitionsAsync(string topic, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            var metadata = await Task.Run(() => _admin.GetMetadata(topic, AdminTimeout), cancellationToken).AnyContext();
            var topicMetadata = metadata.Topics.FirstOrDefault(t => t.Topic == topic);
            if (topicMetadata is { Error.IsError: false, Partitions.Count: > 0 } && topicMetadata.Partitions.All(p => p.Leader >= 0 && !p.Error.IsError))
                return topicMetadata.Partitions.Select(p => new TopicPartition(topic, p.PartitionId)).OrderBy(p => p.Partition.Value).ToList();

            if (Stopwatch.GetElapsedTime(started) > TopicReadyTimeout)
                throw new MessageBusException($"Topic {topic} is not available: {topicMetadata?.Error.Reason ?? "no metadata"}.");

            await Task.Delay(100, cancellationToken).AnyContext();
        }
    }

    private async Task<List<TopicPartitionOffset>> GetEndOffsetsAsync(IReadOnlyList<TopicPartition> partitions, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                var result = await _admin.ListOffsetsAsync(partitions.Select(p => new TopicPartitionOffsetSpec { TopicPartition = p, OffsetSpec = OffsetSpec.Latest() }),
                    new ListOffsetsOptions { RequestTimeout = AdminTimeout }).AnyContext();
                return result.ResultInfos.Select(r => r.TopicPartitionOffsetError.TopicPartitionOffset).ToList();
            }
            catch (ListOffsetsException ex) when (Stopwatch.GetElapsedTime(started) < TopicReadyTimeout)
            {
                Logger.LogDebug(ex, "Retrying listing offsets");
                await Task.Delay(100, cancellationToken).AnyContext();
            }
        }
    }

    /// <summary>A producer with the transport's configuration, adjusted last by <paramref name="adjust"/>.</summary>
    internal IProducer<byte[]?, byte[]> CreateProducer(Action<ProducerConfig>? adjust = null)
    {
        var config = new ProducerConfig(CopyClientConfig())
        {
            BootstrapServers = Options.BootstrapServers,
            EnableIdempotence = true,
            Partitioner = Confluent.Kafka.Partitioner.Murmur2Random,
            MessageMaxBytes = Options.MaxMessageBytes,
            // Topics are created explicitly; never let a send recreate a deleted topic with broker defaults.
            AllowAutoCreateTopics = false
        };

        Options.ConfigureProducer?.Invoke(config);
        adjust?.Invoke(config);
        return new ProducerBuilder<byte[]?, byte[]>(config)
            .SetLogHandler((_, message) => OnLog(message))
            .SetErrorHandler((_, error) => OnError(error))
            .Build();
    }

    private Dictionary<string, string> CopyClientConfig()
    {
        return Options.ClientConfig is { } clientConfig ? new Dictionary<string, string>(clientConfig) : new Dictionary<string, string>();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
}

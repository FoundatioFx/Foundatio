using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// A message transport on RabbitMQ 4. A queue is a quorum queue; a topic is a fanout exchange; a durable subscription
/// is a quorum queue bound to its topic's exchange; an instance subscription is an exclusive, auto-deleted queue bound
/// to the exchange, which the broker removes when its receiver closes or its connection is lost, so nothing is left
/// behind by a crashed process. Every queue and subscription has its own dead-letter queue. Exchanges, queues and
/// bindings are declared on first use.
/// </summary>
/// <remarks>
/// <para>
/// Sends and publishes return once the broker has confirmed every message. A send to a queue that another process
/// deleted after this one declared it is returned by the broker as unroutable, and the transport declares the queue
/// again and sends once more. A publish to a topic without subscriptions is dropped.
/// </para>
/// <para>
/// A delivery is held by its receiver's channel until it is settled: RabbitMQ has no lease to renew, so
/// <see cref="TransportCapabilities.LeaseExpiration"/> is false, and <see cref="IMessageReceiver.RenewAsync"/> reports a
/// delivery whose channel has closed, after which the broker delivers it again. A receiver whose channel closes faults,
/// and Foundatio replaces it. RabbitMQ closes a channel that holds a delivery longer than its <c>consumer_timeout</c>
/// (30 minutes by default), so raise that, for example with a <c>consumer-timeout</c> policy, for handlers that run longer.
/// </para>
/// <para>
/// Quorum queues count a delivery that went back to the queue because its channel closed, but not one released with a
/// requeue. So an abandoned message is sent to its queue again, carrying its delivery count, and its delivery is then
/// acknowledged; a dead-lettered one is sent to the dead-letter queue with its reason, and then acknowledged. If the
/// process stops in between, the message is delivered again as well. Dead letters released by a peek go back to their
/// place in the dead-letter queue.
/// </para>
/// <para>
/// Delays use 28 levels of delay queues, one for each power of two seconds, that every source of the transport's
/// <see cref="RabbitMQMessageTransportOptions.Prefix"/> shares (see <see cref="Capabilities"/>). A delayed message
/// waits on the broker, with no process running, and is delivered within about a second after it is due. A delayed
/// message for a queue or subscription that was deleted is dropped when it comes due, unless it was created again.
/// </para>
/// <para>
/// A due message reaches its target, a queue or a topic's exchange, through the target's landing queue: a quorum queue
/// declared before the target's first delayed message, which passes each message on as it arrives. So a target that
/// refuses messages, such as a full queue whose policy sets <c>overflow: reject-publish</c>, holds up only its own
/// delayed messages (and a subscription its topic's delayed publishes), not those of other targets; the broker tries a
/// refused message again after <c>dead_letter_worker_publisher_confirm_timeout</c> (3 minutes by default). Instance
/// subscriptions have no landing queue, which would outlive them: their delayed retries, and those of a target whose
/// landing queue was deleted, are delivered straight from the delay levels. Deleting a source deletes its landing queue.
/// </para>
/// </remarks>
public sealed class RabbitMQMessageTransport : IMessageTransport
{
    private readonly bool _ownsConnections;
    private readonly IConnection? _connection;
    private readonly ConnectionFactory? _factory;
    private readonly IList<AmqpTcpEndpoint>? _endpoints;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly ConcurrentDictionary<RabbitMQMessageReceiver, byte> _receivers = new();
    private readonly RabbitMQChannelSlot _publisher;
    private readonly RabbitMQSourceStatsReader _stats;
    private readonly RabbitMQManagementClient? _management;
    private IConnection? _publisherConnection;
    private IConnection? _consumerConnection;
    private int _disposed;

    /// <summary>
    /// Creates a transport that opens its own connections from <see cref="RabbitMQMessageTransportOptions.ConnectionString"/>:
    /// one to publish on and one for its receivers, which it closes when it is disposed.
    /// </summary>
    public RabbitMQMessageTransport(RabbitMQMessageTransportOptions options) : this(null, options ?? throw new ArgumentNullException(nameof(options)), ownsConnections: true)
    {
    }

    /// <summary>
    /// Creates a transport that publishes and receives on <paramref name="connection"/>, which stays the caller's. A
    /// connection with automatic recovery is fine: a receiver whose channel closed during an outage faults and is replaced.
    /// </summary>
    public RabbitMQMessageTransport(IConnection connection, RabbitMQMessageTransportOptions? options = null)
        : this(connection ?? throw new ArgumentNullException(nameof(connection)), options ?? new RabbitMQMessageTransportOptions(), ownsConnections: false)
    {
    }

    private RabbitMQMessageTransport(IConnection? connection, RabbitMQMessageTransportOptions options, bool ownsConnections)
    {
        RabbitMQMessageTransportNames.ValidatePrefix(options.Prefix, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxMessageBytes, RabbitMQMessageTransportCodec.EncodingReserve * 2, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.ReleaseDelay, TimeSpan.Zero, nameof(options));
        if (options.PublishTimeout <= TimeSpan.Zero && options.PublishTimeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(options), options.PublishTimeout, "PublishTimeout must be positive, or Timeout.InfiniteTimeSpan.");

        Options = options;
        Names = new RabbitMQMessageTransportNames(options.Prefix);
        TimeProvider = options.TimeProvider ?? TimeProvider.System;
        LoggerFactory = options.LoggerFactory ?? NullLoggerFactory.Instance;
        Logger = LoggerFactory.CreateLogger<RabbitMQMessageTransport>();
        _connection = connection;
        _ownsConnections = ownsConnections;
        if (ownsConnections)
            (_factory, _endpoints) = CreateFactory(options);

        var maxDelay = options.DelayedDelivery ? RabbitMQDelayLevels.MaxDelay : TimeSpan.Zero;
        Capabilities = new TransportCapabilities
        {
            MaxDelay = maxDelay,
            MaxPublishDelay = maxDelay,
            MaxRetryDelay = maxDelay,
            MinRetryDelay = options.DelayedDelivery ? RabbitMQDelayLevels.Resolution : TimeSpan.Zero,
            MaxSendBatchSize = 1000,
            MaxReceiveBatchSize = 100,
            MaxMessageSize = options.MaxMessageBytes - RabbitMQMessageTransportCodec.EncodingReserve,
            MaxLeaseDuration = null,
            LeaseExpiration = false,
            OrderedGroups = false,
            Deduplication = false
        };

        if (options.ManagementEndpoint is { } managementEndpoint)
        {
            _management = new RabbitMQManagementClient(managementEndpoint, options.ConnectionString);
            if (options.DelayedDelivery)
                DelayInspector = new RabbitMQDelayInspector(_management, Names, options.Prefix, Logger, TimeProvider, options.DelayCheckInterval, options.DelayStallWarningAfter);
        }

        _publisher = new RabbitMQChannelSlot(async ct => await (await GetConnectionAsync(consumer: false, ct).AnyContext())
            .CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct).AnyContext());
        Topology = new RabbitMQMessageTransportTopology(Names, options.DelayedDelivery,
            async ct => await (await GetConnectionAsync(consumer: false, ct).AnyContext()).CreateChannelAsync(cancellationToken: ct).AnyContext(), DelayInspector, Logger);
        _stats = new RabbitMQSourceStatsReader(this, _management, DelayInspector);
    }

    /// <summary>
    /// Send, publish and retry delays of up to 2^28 - 1 seconds (about 8.5 years) when
    /// <see cref="RabbitMQMessageTransportOptions.DelayedDelivery"/> is on, rounded up to whole seconds; retries shorter
    /// than a second are held by Foundatio in-process. Without delayed delivery there are none. Messages may be
    /// <see cref="RabbitMQMessageTransportOptions.MaxMessageBytes"/> less 16 KiB, which the transport keeps for headers it
    /// moves into the body. Leases do not expire. Groups are not ordered and messages are not deduplicated.
    /// </summary>
    public TransportCapabilities Capabilities { get; }

    internal RabbitMQMessageTransportOptions Options { get; }
    internal RabbitMQMessageTransportNames Names { get; }
    internal RabbitMQMessageTransportTopology Topology { get; }
    internal RabbitMQDelayInspector? DelayInspector { get; }
    internal TimeProvider TimeProvider { get; }
    internal ILoggerFactory LoggerFactory { get; }
    internal ILogger Logger { get; }
    internal bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    /// <inheritdoc />
    /// <remarks>
    /// The queue, or the topic's exchange, is declared first, so a send before any receiver exists is kept. A message to
    /// one subscription goes straight to that subscription's queue.
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

        string target = destination.Kind switch
        {
            MessageDestinationKind.Queue => Names.Queue(destination.Name),
            MessageDestinationKind.Subscription => Names.Subscription(destination.Name, destination.SubscriptionName!),
            MessageDestinationKind.Topic => Names.Topic(destination.Name),
            _ => throw new ArgumentOutOfRangeException(nameof(destination), destination.Kind, "Unknown destination kind.")
        };
        bool isTopic = destination.Kind == MessageDestinationKind.Topic;

        int headerTableLimit = await GetHeaderTableLimitAsync(cancellationToken).AnyContext();
        long maxSize = Capabilities.MaxMessageSize!.Value;
        var now = TimeProvider.GetUtcNow();
        var publishes = new List<Publish>(messages.Count);
        bool delayed = false;
        foreach (var message in messages)
        {
            ArgumentNullException.ThrowIfNull(message, nameof(messages));
            long size = MessageSize.Of(message);
            if (size > maxSize)
                throw new MessageTooLargeException($"Message {message.MessageId} is {size} bytes with headers and ids, which exceeds the transport limit of {maxSize} bytes.") { Size = size, Limit = maxSize };

            var (properties, body) = RabbitMQMessageTransportCodec.Encode(message.MessageId, message.Body, message.Headers, message.GroupKey, 0, now, headerTableLimit);
            if (body.Length > Options.MaxMessageBytes)
                throw new MessageTooLargeException($"Message {message.MessageId} encodes to {body.Length} bytes, which exceeds MaxMessageBytes of {Options.MaxMessageBytes}.") { Size = body.Length, Limit = Options.MaxMessageBytes };

            long delaySeconds = message.DeliverAt is { } deliverAt ? RabbitMQDelayLevels.ToSeconds(deliverAt - now) : 0;
            if (delaySeconds > 0)
            {
                if (!Options.DelayedDelivery)
                    throw new ArgumentException("Delayed delivery is turned off for this transport.", nameof(messages));

                publishes.Add(Publish.Delayed(Names, target, delaySeconds, properties, body));
                delayed = true;
                continue;
            }

            publishes.Add(isTopic ? new Publish(target, String.Empty, false, properties, body) : new Publish(String.Empty, target, true, properties, body));
        }

        try
        {
            await PublishAsync(publishes, async ct =>
            {
                switch (destination.Kind)
                {
                    case MessageDestinationKind.Queue:
                        await Topology.EnsureQueueAsync(target, ct).AnyContext();
                        break;
                    case MessageDestinationKind.Subscription:
                        await Topology.EnsureSubscriptionAsync(Names.Topic(destination.Name), target, ct).AnyContext();
                        break;
                    default:
                        await Topology.EnsureTopicAsync(target, ct).AnyContext();
                        break;
                }

                if (delayed)
                    await Topology.EnsureLandingAsync(target, ct).AnyContext();
            }, target, cancellationToken).AnyContext();
        }
        catch (MessageBusException ex) when (ex is not MessageTooLargeException)
        {
            throw new MessageBusException($"Could not send to {destination}: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns once the receiver's consumer is registered, so it gets every message already in the source. An
    /// instance subscription's queue is declared on the receiver's own channel and bound to the topic before then. A
    /// queue or exchange that another process deleted, or the broker lost, after this transport declared it is
    /// declared again.
    /// </remarks>
    public async Task<IMessageReceiver> CreateReceiverAsync(MessageSource source, ReceiverOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.PrefetchCount, 1, nameof(options));
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        string queue = source.Kind switch
        {
            MessageSourceKind.Queue or MessageSourceKind.DeadLetter or MessageSourceKind.Subscription => Names.For(source),
            MessageSourceKind.InstanceSubscription => Names.Instance(source.Topic!, source.Name),
            _ => throw new ArgumentOutOfRangeException(nameof(source), source.Kind, "Unknown source kind.")
        };
        string? topicExchange = source.Kind == MessageSourceKind.InstanceSubscription ? Names.Topic(source.Topic!) : null;

        for (int attempt = 1; ; attempt++)
        {
            RabbitMQMessageReceiver? receiver = null;
            try
            {
                await EnsureSourceAsync(source, queue, topicExchange, cancellationToken).AnyContext();
                var connection = await GetConnectionAsync(consumer: true, cancellationToken).AnyContext();
                receiver = new RabbitMQMessageReceiver(this, source, queue, topicExchange, options);
                _receivers.TryAdd(receiver, 0);
                await receiver.StartAsync(connection, cancellationToken).AnyContext();
                ThrowIfDisposed();
                return receiver;
            }
            catch (MessageBusException ex) when (attempt == 1 && ex.InnerException is OperationInterruptedException { ShutdownReason.ReplyCode: 404 })
            {
                // Another process deleted something this transport declared, or the broker lost its definitions, so
                // everything it remembers declaring is declared again: the exchanges a queue binds to as well.
                Logger.LogDebug(ex, "What {Source} receives from no longer exists; declaring it again", source);
                if (receiver is not null)
                    await receiver.DisposeAsync().AnyContext();

                Topology.Forget(queue);
                Topology.ForgetExchanges();
            }
            catch
            {
                if (receiver is not null)
                    await receiver.DisposeAsync().AnyContext();

                throw;
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Deletes the source's queue and its dead-letter queue, or only the dead-letter queue, with their landing queues,
    /// which a process with delayed delivery may have declared even when this one has it off. Receivers open on a deleted queue, in any process, are cancelled by the broker and fault, so Foundatio replaces
    /// them and the queue is declared again. A subscription's topic exchange, and its landing queue, are kept.
    /// </remarks>
    public async Task DeleteAsync(MessageSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfDisposed();

        string[] queues = source.Kind switch
        {
            MessageSourceKind.Queue or MessageSourceKind.Subscription => [Names.For(source), Names.DeadLetter(source)],
            MessageSourceKind.DeadLetter => [Names.DeadLetter(source)],
            _ => throw new ArgumentException("Instance subscriptions are removed by disposing their receiver.", nameof(source))
        };

        foreach (string queue in queues)
            await Topology.DeleteQueueAsync(queue, cancellationToken).AnyContext();

        Logger.LogInformation("Deleted {Source}", source);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <see cref="MessageSourceStats.Queued"/> is the queue's ready messages, and <see cref="MessageSourceStats.DeadLettered"/>
    /// the dead-letter queue's. Without <see cref="RabbitMQMessageTransportOptions.ManagementEndpoint"/>, they come from
    /// passive declares, one round trip each, and are current; <see cref="MessageSourceStats.InFlight"/> is then null,
    /// and messages delivered to a receiver and not settled are counted nowhere (AMQP does not report them). With the
    /// management API, <see cref="MessageSourceStats.InFlight"/> is the queue's unacknowledged messages, including those a
    /// receiver has prefetched but not handed out yet, dead letters include those being peeked, and every count lags by
    /// up to the broker's statistics interval (5 seconds by default).
    /// </para>
    /// <para>
    /// <see cref="MessageSourceStats.Delayed"/> is null: delayed messages and delayed retries wait in delay queues every
    /// source shares, so they are not counted. Nothing is declared; a source that does not exist counts as empty.
    /// </para>
    /// <para>
    /// With the management API and delayed delivery, a call also has the delay levels and landing queues inspected, at
    /// most every 15 seconds and without waiting for it, and a warning is logged for a level or landing queue whose
    /// expired messages have waited a minute or more for their next hop, or a queue whose policy holds up delayed delivery.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">The source is a dead-letter source.</exception>
    public Task<MessageSourceStats?> GetStatsAsync(MessageSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfDisposed();
        if (source.Kind == MessageSourceKind.DeadLetter)
            throw new ArgumentException("Dead letters are counted in the DeadLettered count of the source they belong to.", nameof(source));

        if (source.Kind == MessageSourceKind.InstanceSubscription)
            return Task.FromResult<MessageSourceStats?>(null);

        DelayInspector?.CheckDelaysInBackground();
        return _stats.GetAsync(source, Names.For(source), Names.DeadLetter(source), cancellationToken);
    }

    /// <summary>Closes every receiver (their unsettled messages go back to their queues), then the transport's channels and the connections it opened.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        await Task.WhenAll(_receivers.Keys.Select(r => r.DisposeAsync().AsTask())).AnyContext();
        await _publisher.DisposeAsync().AnyContext();
        await Topology.DisposeAsync().AnyContext();
        await _stats.DisposeAsync().AnyContext();
        _management?.Dispose();

        if (!_ownsConnections)
            return;

        foreach (var connection in new[] { _publisherConnection, _consumerConnection })
        {
            if (connection is null)
                continue;

            try
            {
                await connection.CloseAsync().AnyContext();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Logger.LogDebug(ex, "Error closing RabbitMQ connection");
            }

            await connection.DisposeAsync().AnyContext();
        }
    }

    /// <summary>
    /// Publishes every message and waits for the broker's confirms, for at most <see cref="RabbitMQMessageTransportOptions.PublishTimeout"/>.
    /// Messages the broker returned as unroutable, or that a channel closed on, are sent once more after
    /// <paramref name="ensure"/> declares their target again.
    /// </summary>
    internal async Task PublishAsync(IReadOnlyList<Publish> publishes, Func<CancellationToken, Task> ensure, string target, CancellationToken cancellationToken)
    {
        if (Options.PublishTimeout == Timeout.InfiniteTimeSpan)
        {
            await PublishUntilConfirmedAsync(publishes, ensure, target, cancellationToken).AnyContext();
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Options.PublishTimeout);
        try
        {
            await PublishUntilConfirmedAsync(publishes, ensure, target, timeout.Token).AnyContext();
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            // Not waited for: closing a connection to a node that stopped answering takes seconds.
            _ = DropPublisherConnectionAsync();
            throw new MessageBusException($"RabbitMQ did not confirm {publishes.Count} messages to {target} within {Options.PublishTimeout}; they may or may not have been delivered.", ex);
        }
    }

    /// <summary>
    /// Aborts the publishing connection the transport opened, so the next send connects again instead of waiting on a
    /// node that stopped confirming. A connection the caller gave the transport is left alone.
    /// </summary>
    private async Task DropPublisherConnectionAsync()
    {
        if (!_ownsConnections || Volatile.Read(ref _publisherConnection) is not { IsOpen: true } connection)
            return;

        Logger.LogWarning("RabbitMQ did not confirm publishes within {PublishTimeout}; dropping the publishing connection to {Host}:{Port} so the next send connects again",
            Options.PublishTimeout, connection.Endpoint.HostName, connection.Endpoint.Port);
        try
        {
            await connection.AbortAsync(TimeSpan.FromSeconds(1)).AnyContext();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Logger.LogDebug(ex, "Error aborting the RabbitMQ publishing connection");
        }
    }

    private async Task PublishUntilConfirmedAsync(IReadOnlyList<Publish> publishes, Func<CancellationToken, Task> ensure, string target, CancellationToken cancellationToken)
    {
        var pending = publishes;
        for (int attempt = 1; ; attempt++)
        {
            await ensure(cancellationToken).AnyContext();
            IChannel channel;
            try
            {
                channel = await _publisher.GetAsync(cancellationToken).AnyContext();
            }
            catch (Exception ex) when (ex is BrokerUnreachableException or OperationInterruptedException or AlreadyClosedException)
            {
                throw new MessageBusException($"Could not connect to RabbitMQ: {ex.Message}", ex);
            }

            var tasks = new Task[pending.Count];
            for (int i = 0; i < pending.Count; i++)
            {
                var publish = pending[i];
                tasks[i] = channel.BasicPublishAsync(publish.Exchange, publish.RoutingKey, publish.Mandatory, publish.Properties, publish.Body, cancellationToken).AsTask();
            }

            try
            {
                await Task.WhenAll(tasks).AnyContext();
                return;
            }
            catch
            {
                // Inspected below, task by task.
            }

            cancellationToken.ThrowIfCancellationRequested();
            var failed = new List<Publish>();
            Exception? error = null;
            bool retryable = true;
            for (int i = 0; i < tasks.Length; i++)
            {
                if (tasks[i].Exception?.InnerException is not { } exception)
                    continue;

                failed.Add(pending[i]);
                error ??= exception;
                retryable &= exception is PublishReturnException or AlreadyClosedException or OperationInterruptedException;
            }

            if (failed.Count == 0)
                return;

            if (!retryable || attempt >= 2)
            {
                string reason = error is PublishReturnException returned ? $"the broker returned it ({returned.ReplyCode} {returned.ReplyText})" : error!.Message;
                throw new MessageBusException($"RabbitMQ did not confirm {failed.Count} of {publishes.Count} messages to {target}: {reason}", error);
            }

            Logger.LogDebug(error, "RabbitMQ did not take {Count} messages for {Target}; declaring it again and sending them once more", failed.Count, target);
            Topology.Forget(target);
            Topology.ForgetExchanges();
            pending = failed;
        }
    }

    /// <summary>
    /// Sends a received message again, to its own queue (an abandon, carrying its delivery count) or to a dead-letter
    /// queue, after <paramref name="delay"/>. An instance subscription's queue is not declared: it exists while its
    /// receiver is open, and only on that receiver's connection. Nor does it get a landing queue, which would outlive it.
    /// </summary>
    internal async Task PublishCopyAsync(string queue, bool declareQueue, RabbitMQIncomingMessage message, IReadOnlyDictionary<string, string> headers,
        int previousDeliveries, TimeSpan delay, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        int headerTableLimit = await GetHeaderTableLimitAsync(cancellationToken).AnyContext();
        var (properties, body) = RabbitMQMessageTransportCodec.Encode(message.MessageId, message.Body, headers, message.GroupKey, previousDeliveries,
            message.EnqueuedAt ?? TimeProvider.GetUtcNow(), headerTableLimit);
        long delaySeconds = Options.DelayedDelivery ? RabbitMQDelayLevels.ToSeconds(delay) : 0;
        var publish = delaySeconds > 0 ? Publish.Delayed(Names, queue, delaySeconds, properties, body) : new Publish(String.Empty, queue, true, properties, body);

        await PublishAsync([publish], async ct =>
        {
            if (declareQueue)
                await Topology.EnsureQueueAsync(queue, ct).AnyContext();
            if (delaySeconds > 0)
            {
                if (declareQueue)
                    await Topology.EnsureLandingAsync(queue, ct).AnyContext();
                else
                    await Topology.EnsureDelaysAsync(ct).AnyContext();
            }
        }, queue, cancellationToken).AnyContext();
    }

    internal async Task<int> GetHeaderTableLimitAsync(CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync(consumer: false, cancellationToken).AnyContext();
        return RabbitMQMessageTransportCodec.GetHeaderTableLimit(connection.FrameMax);
    }

    internal async Task<IConnection> GetConnectionAsync(bool consumer, CancellationToken cancellationToken)
    {
        if (_connection is not null)
            return _connection;

        var current = consumer ? Volatile.Read(ref _consumerConnection) : Volatile.Read(ref _publisherConnection);
        if (current is { IsOpen: true })
            return current;

        await _connectionLock.WaitAsync(cancellationToken).AnyContext();
        try
        {
            ThrowIfDisposed();
            current = consumer ? _consumerConnection : _publisherConnection;
            if (current is { IsOpen: true })
                return current;

            if (current is not null)
                await current.DisposeAsync().AnyContext();

            string role = consumer ? "receivers" : "publisher";
            var connection = await ConnectAsync($"Foundatio {Options.Prefix} transport ({role})", cancellationToken).AnyContext();

            connection.ConnectionShutdownAsync += (_, args) =>
            {
                if (!IsDisposed)
                    Logger.LogWarning("RabbitMQ {Role} connection closed: {ReplyCode} {ReplyText}", role, args.ReplyCode, args.ReplyText);

                return Task.CompletedTask;
            };

            if (consumer)
                Volatile.Write(ref _consumerConnection, connection);
            else
                Volatile.Write(ref _publisherConnection, connection);

            return connection;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>
    /// Connects to the first host, in random order, that completes the AMQP handshake within the connection factory's
    /// <see cref="ConnectionFactory.HandshakeContinuationTimeout"/>. RabbitMQ.Client moves on to the next host only when
    /// a connection is refused, and waits as long as the caller's token for a host that accepts the connection and
    /// never starts the handshake (a paused node, or a load balancer whose backend is down), so each host is tried on
    /// its own, with that timeout.
    /// </summary>
    private async Task<IConnection> ConnectAsync(string name, CancellationToken cancellationToken)
    {
        var factory = _factory!;
        var endpoints = _endpoints!.ToArray();
        Random.Shared.Shuffle(endpoints);
        Exception? failure = null;
        foreach (var endpoint in endpoints)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(factory.HandshakeContinuationTimeout);
            var connecting = factory.CreateConnectionAsync([endpoint], name, attempt.Token);
            try
            {
                // The client takes seconds more to give up once cancelled, so it is not waited for.
                return await connecting.WaitAsync(attempt.Token).AnyContext();
            }
            catch (Exception ex) when (ex is BrokerUnreachableException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                if (!connecting.IsCompleted)
                    _ = CloseLateConnectionAsync(connecting);

                failure = ex is OperationCanceledException
                    ? new TimeoutException($"{endpoint.HostName}:{endpoint.Port} did not complete the AMQP handshake within {factory.HandshakeContinuationTimeout}.", ex)
                    : ex;
                if (endpoints.Length > 1)
                    Logger.LogWarning("Could not connect to RabbitMQ at {Host}:{Port}, trying the next host: {Reason}", endpoint.HostName, endpoint.Port, Reason(failure));
            }
            catch (OperationCanceledException) when (!connecting.IsCompleted)
            {
                _ = CloseLateConnectionAsync(connecting);
                throw;
            }
        }

        string hosts = String.Join(", ", endpoints.Select(e => $"{e.HostName}:{e.Port}"));
        throw new MessageBusException($"Could not connect to RabbitMQ at {hosts}: {Reason(failure!)}", failure!);

        static string Reason(Exception ex) => ex is BrokerUnreachableException { InnerException: { } inner } ? inner.Message : ex.Message;
    }

    private static async Task CloseLateConnectionAsync(Task<IConnection> connecting)
    {
        try
        {
            await (await connecting.AnyContext()).DisposeAsync().AnyContext();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The attempt that timed out failed after all.
        }
    }

    internal void Remove(RabbitMQMessageReceiver receiver) => _receivers.TryRemove(receiver, out _);

    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);

    /// <summary>Declares what a receiver of <paramref name="source"/> consumes, except an instance subscription's own queue, which its receiver declares.</summary>
    private async Task EnsureSourceAsync(MessageSource source, string queue, string? topicExchange, CancellationToken cancellationToken)
    {
        switch (source.Kind)
        {
            case MessageSourceKind.Queue:
            case MessageSourceKind.DeadLetter:
                await Topology.EnsureQueueAsync(queue, cancellationToken).AnyContext();
                break;
            case MessageSourceKind.Subscription:
                await Topology.EnsureSubscriptionAsync(Names.Topic(source.Topic!), queue, cancellationToken).AnyContext();
                break;
            default:
                await Topology.EnsureTopicAsync(topicExchange!, cancellationToken).AnyContext();
                if (Options.DelayedDelivery)
                    await Topology.EnsureDelaysAsync(cancellationToken).AnyContext();
                break;
        }
    }

    private static (ConnectionFactory Factory, IList<AmqpTcpEndpoint> Endpoints) CreateFactory(RabbitMQMessageTransportOptions options)
    {
        if (String.IsNullOrWhiteSpace(options.ConnectionString))
            throw new ArgumentException("A ConnectionString is required when the transport opens its own connections.", nameof(options));
        if (!Uri.TryCreate(options.ConnectionString, UriKind.Absolute, out var uri) || uri.Scheme is not ("amqp" or "amqps"))
            throw new ArgumentException("The ConnectionString must be an amqp:// or amqps:// URI.", nameof(options));

        var factory = new ConnectionFactory
        {
            Uri = uri,
            AutomaticRecoveryEnabled = false,
            TopologyRecoveryEnabled = false,
            MaxInboundMessageBodySize = (uint)Math.Max(options.MaxMessageBytes + 1024 * 1024, 64 * 1024 * 1024)
        };
        options.ConfigureConnectionFactory?.Invoke(factory);
        factory.AutomaticRecoveryEnabled = false;
        factory.TopologyRecoveryEnabled = false;

        return (factory, RabbitMQEndpointResolver.CreateEndpoints(factory, options.Hosts?.ToList()));
    }

    /// <summary>One message to publish: where to, and its encoded properties and body.</summary>
    internal readonly record struct Publish(string Exchange, string RoutingKey, bool Mandatory, BasicProperties Properties, ReadOnlyMemory<byte> Body)
    {
        /// <summary>
        /// A message that reaches <paramref name="target"/>, a queue or a topic's exchange, after <paramref name="seconds"/>.
        /// It enters at the level of the delay's highest set bit, whose exchange routes it to its own queue, so it skips
        /// the levels above, which would only pass it on.
        /// </summary>
        public static Publish Delayed(RabbitMQMessageTransportNames names, string target, long seconds, BasicProperties properties, ReadOnlyMemory<byte> body)
        {
            // The entry level's queue takes every message, so an unroutable delayed message means the levels were deleted.
            return new Publish(names.DelayLevel(RabbitMQDelayLevels.EntryLevel(seconds)), RabbitMQDelayLevels.RoutingKey(seconds, target), true, properties, body);
        }
    }
}

using System;
using System.Collections.Generic;

namespace Foundatio.Messaging;

public class RabbitMQMessageBusOptions : SharedMessageBusOptions
{
    /// <summary>
    /// The connection string. See https://www.rabbitmq.com/uri-spec.html for more information.
    /// Provides credentials and vhost. When Hosts is specified, the host in the connection string is ignored.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// List of hosts for failover. When specified, the client will try each host in order until one succeeds.
    /// Format: "hostname" or "hostname:port" (default port is 5672, or 5671 for amqps).
    /// If not specified, the host from ConnectionString is used.
    /// </summary>
    public IList<string>? Hosts { get; set; }

    /// <summary>
    /// The default message time to live. The value of the expiration field describes the TTL period in milliseconds.
    /// </summary>
    public TimeSpan? DefaultMessageTimeToLive { get; set; }

    /// <summary>
    /// Arguments passed to QueueDeclare. Some brokers use it to implement additional features like message TTL.
    /// </summary>
    public IDictionary<string, object?>? Arguments { get; set; }

    /// <summary>
    /// Durable (will survive a broker restart)
    /// </summary>
    public bool IsDurable { get; set; } = true;

    /// <summary>
    /// Whether the subscription queue is exclusive to this message bus instance.
    /// </summary>
    public bool IsSubscriptionQueueExclusive { get; set; } = true;

    /// <summary>
    /// Whether the subscription queue should be automatically deleted.
    /// </summary>
    public bool SubscriptionQueueAutoDelete { get; set; } = true;

    /// <summary>
    /// The name of the subscription queue this message bus instance will listen on.
    /// </summary>
    public string SubscriptionQueueName { get; set; } = String.Empty;

    /// <summary>
    /// How messages should be acknowledged.
    /// </summary>
    public AcknowledgementStrategy AcknowledgementStrategy { get; set; } = AcknowledgementStrategy.FireAndForget;

    /// <summary>
    /// Consumer prefetch count. Limits the number of unacknowledged messages a consumer can have.
    /// Set to 0 (default) which will prefetch all available messages (unbounded).
    /// Recommended values: 5-50 for most scenarios, higher for fast processing, lower for slow/heavy processing or large message sizes.
    /// </summary>
    public ushort PrefetchCount { get; set; }

    /// <summary>
    /// Consumer prefetch size in bytes. Limits the total size of unacknowledged messages a consumer can have.
    /// Set to 0 (default) for no size limit. This provides additional flow control beyond message count.
    /// </summary>
    public uint PrefetchSize { get; set; }

    /// <summary>
    /// Whether QoS settings apply globally to the connection or just to the channel.
    /// When true, the QoS settings apply to all consumers on the connection.
    /// When false (default), the QoS settings apply only to consumers on this channel.
    /// </summary>
    [Obsolete("Global QoS is deprecated in RabbitMQ 4.3+ and will be removed in a future version. Use per-channel prefetch (GlobalQos = false) instead.")]
    public bool GlobalQos { get; set; }

    /// <summary>
    /// Sets the maximum number of times a message can be delivered (retried) before it is discarded. If using
    /// classic queues, we will republish the message with an x-delivery-count header and acknowledge the previous message.
    ///
    /// Setting this to -1 means there is no limit on the number of deliveries.
    /// </summary>
    public long DeliveryLimit { get; set; } = 2;

    /// <summary>
    /// When true, PublishAsync waits for broker confirmation before returning.
    /// This guarantees the message reached the broker but adds latency per publish.
    /// Performance impact varies by workload - use async/pipelining patterns for best results.
    /// Default: false (fire-and-forget publishing for backward compatibility).
    /// See: https://www.rabbitmq.com/docs/confirms#publisher-confirms
    /// </summary>
    public bool PublisherConfirmsEnabled { get; set; }

    /// <summary>
    /// Maximum time each publish attempt will wait for connection recovery before failing.
    /// During a transient connection drop, publishes suspend (rather than failing immediately)
    /// and resume automatically when the connection recovers.
    /// Note: The resilience policy may retry failed attempts (default: 3 attempts with exponential backoff),
    /// so total wall-clock time can exceed this value.
    /// Set to TimeSpan.Zero to disable (fail immediately on connection drop, like pre-fix behavior).
    /// Default: 10 seconds (covers one full NetworkRecoveryInterval cycle with margin).
    /// </summary>
    public TimeSpan PublishRecoveryTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Heartbeat timeout negotiated with the broker. Controls how quickly dead TCP connections are detected.
    /// Lower values detect failures faster but may cause false positives on congested networks.
    /// Set to TimeSpan.Zero to disable heartbeats (not recommended for production).
    /// Default: null (uses client library default of 60 seconds).
    /// See: https://www.rabbitmq.com/docs/heartbeats
    /// </summary>
    public TimeSpan? RequestedHeartbeat { get; set; }

    /// <summary>
    /// Time between automatic connection recovery attempts after a network failure.
    /// Higher values reduce reconnection pressure on the broker during outages but increase downtime.
    /// Default: null (uses client library default of 5 seconds).
    /// See: https://www.rabbitmq.com/client-libraries/dotnet-api-guide#connection-recovery
    /// </summary>
    public TimeSpan? NetworkRecoveryInterval { get; set; }

    /// <summary>
    /// Dead letter exchange name. Messages that exceed the delivery limit or are rejected
    /// will be routed to this exchange instead of being dropped.
    /// Set via the x-dead-letter-exchange queue argument.
    /// See: https://www.rabbitmq.com/docs/dlx
    /// </summary>
    public string? DeadLetterExchange { get; set; }

    /// <summary>
    /// Routing key used when dead-lettering messages. If not set, the original routing key is preserved.
    /// Only effective when DeadLetterExchange is also set.
    /// Set via the x-dead-letter-routing-key queue argument.
    /// </summary>
    public string? DeadLetterRoutingKey { get; set; }

    /// <summary>
    /// Dead-letter strategy for quorum queues. Controls whether messages are transferred
    /// to the DLX with at-most-once (default, may lose messages) or at-least-once (guaranteed delivery)
    /// semantics. At-least-once requires Overflow to be set to RejectPublish.
    /// Set via the x-dead-letter-strategy queue argument.
    /// See: https://www.rabbitmq.com/docs/quorum-queues#dead-lettering
    /// </summary>
    public DeadLetterStrategy? DeadLetterStrategy { get; set; }

    /// <summary>
    /// Queue overflow behavior when the queue reaches its max length.
    /// Must be set to RejectPublish when using at-least-once dead-lettering.
    /// Set via the x-overflow queue argument.
    /// See: https://www.rabbitmq.com/docs/maxlength#overflow-behaviour
    /// </summary>
    public QueueOverflowBehavior? Overflow { get; set; }

    /// <summary>
    /// Consumer timeout in milliseconds for quorum queues (RabbitMQ 4.3+).
    /// Limits how long a consumer can hold unacknowledged messages before the broker returns them.
    /// When exceeded, messages are requeued and the consumer is cancelled gracefully.
    /// Set via the x-consumer-timeout queue argument.
    /// Default: null (uses broker default, typically 30 minutes).
    /// See: https://www.rabbitmq.com/docs/consumers#acknowledgement-timeout
    /// </summary>
    public TimeSpan? ConsumerTimeout { get; set; }

    /// <summary>
    /// When true, only one consumer at a time will receive messages from the queue.
    /// Other consumers act as standby and automatically take over if the active consumer disconnects.
    /// Useful for strict message ordering with automatic failover.
    /// Set via the x-single-active-consumer queue argument.
    /// See: https://www.rabbitmq.com/docs/consumers#single-active-consumer
    /// </summary>
    public bool SingleActiveConsumer { get; set; }

    /// <summary>
    /// Maximum number of priority levels for the queue (1-32).
    /// Messages published with a higher priority value are delivered to consumers before lower-priority messages.
    /// RabbitMQ 4.3+ quorum queues support 32 strict priority levels.
    /// Set via the x-max-priority queue argument.
    /// See: https://www.rabbitmq.com/docs/priority
    /// </summary>
    public byte? MaxPriority { get; set; }

    /// <summary>
    /// Configures native delayed retry for quorum queues (RabbitMQ 4.3+).
    /// When set, rejected/failed messages are held in a delayed state before becoming available again.
    /// The delay uses linear backoff: min(min_delay * delivery_count, max_delay).
    /// Requires quorum queues. Set via x-delayed-retry-type queue argument.
    /// Default: null (not configured).
    /// See: https://www.rabbitmq.com/docs/quorum-queues#delayed-retries
    /// </summary>
    public DelayedRetryType? DelayedRetryType { get; set; }

    /// <summary>
    /// Minimum delay in milliseconds for native delayed retry (RabbitMQ 4.3+).
    /// The actual delay is: min(DelayedRetryMin * delivery_count, DelayedRetryMax).
    /// Only effective when DelayedRetryType is set.
    /// Set via x-delayed-retry-min queue argument.
    /// </summary>
    public int? DelayedRetryMin { get; set; }

    /// <summary>
    /// Maximum delay in milliseconds for native delayed retry (RabbitMQ 4.3+).
    /// Caps the linear backoff so delays don't grow unbounded.
    /// Only effective when DelayedRetryType is set.
    /// Set via x-delayed-retry-max queue argument.
    /// </summary>
    public int? DelayedRetryMax { get; set; }
}

public class RabbitMQMessageBusOptionsBuilder : SharedMessageBusOptionsBuilder<RabbitMQMessageBusOptions, RabbitMQMessageBusOptionsBuilder>
{
    public RabbitMQMessageBusOptionsBuilder ConnectionString(string? connectionString)
    {
        Target.ConnectionString = String.IsNullOrWhiteSpace(connectionString) ? null : connectionString;
        return this;
    }

    /// <summary>
    /// Sets the list of hosts for failover support. Replaces the host from ConnectionString.
    /// </summary>
    /// <param name="hosts">Hostnames in format "hostname" or "hostname:port"</param>
    public RabbitMQMessageBusOptionsBuilder Hosts(params string[] hosts)
    {
        Target.Hosts = hosts ?? throw new ArgumentNullException(nameof(hosts));
        return this;
    }

    /// <summary>
    /// Sets the list of hosts for failover support. Replaces the host from ConnectionString.
    /// </summary>
    /// <param name="hosts">Hostnames in format "hostname" or "hostname:port"</param>
    public RabbitMQMessageBusOptionsBuilder Hosts(IList<string> hosts)
    {
        Target.Hosts = hosts ?? throw new ArgumentNullException(nameof(hosts));
        return this;
    }

    public RabbitMQMessageBusOptionsBuilder DefaultMessageTimeToLive(TimeSpan defaultMessageTimeToLive)
    {
        Target.DefaultMessageTimeToLive = defaultMessageTimeToLive;
        return this;
    }

    public RabbitMQMessageBusOptionsBuilder Arguments(IDictionary<string, object?> arguments)
    {
        Target.Arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
        return this;
    }

    public RabbitMQMessageBusOptionsBuilder IsDurable(bool isDurable)
    {
        Target.IsDurable = isDurable;
        return this;
    }

    public RabbitMQMessageBusOptionsBuilder IsSubscriptionQueueExclusive(bool isExclusive)
    {
        Target.IsSubscriptionQueueExclusive = isExclusive;
        return this;
    }

    public RabbitMQMessageBusOptionsBuilder SubscriptionQueueAutoDelete(bool autoDelete)
    {
        Target.SubscriptionQueueAutoDelete = autoDelete;
        return this;
    }

    public RabbitMQMessageBusOptionsBuilder SubscriptionQueueName(string subscriptionQueueName)
    {
        Target.SubscriptionQueueName = subscriptionQueueName;
        return this;
    }

    public RabbitMQMessageBusOptionsBuilder AcknowledgementStrategy(AcknowledgementStrategy acknowledgementStrategy)
    {
        Target.AcknowledgementStrategy = acknowledgementStrategy;
        return this;
    }

    public RabbitMQMessageBusOptionsBuilder PrefetchCount(ushort prefetchCount)
    {
        Target.PrefetchCount = prefetchCount;
        return this;
    }

    public RabbitMQMessageBusOptionsBuilder PrefetchSize(uint prefetchSize)
    {
        Target.PrefetchSize = prefetchSize;
        return this;
    }

    [Obsolete("Global QoS is deprecated in RabbitMQ 4.3+ and will be removed in a future version. Use per-channel prefetch (GlobalQos = false) instead.")]
    public RabbitMQMessageBusOptionsBuilder GlobalQos(bool globalQos)
    {
#pragma warning disable CS0618
        Target.GlobalQos = globalQos;
#pragma warning restore CS0618
        return this;
    }

    public RabbitMQMessageBusOptionsBuilder DeliveryLimit(long deliveryLimit)
    {
        Target.DeliveryLimit = deliveryLimit;

        Target.Arguments ??= new Dictionary<string, object?>();
        Target.Arguments["x-delivery-limit"] = deliveryLimit;

        return this;
    }

    /// <summary>
    /// Enables publisher confirms, which guarantees the message reached the broker.
    /// When enabled, PublishAsync waits for broker confirmation before returning.
    /// See: https://www.rabbitmq.com/docs/confirms#publisher-confirms
    /// </summary>
    /// <param name="enabled">Whether to enable publisher confirms. Default: true.</param>
    /// <returns>The builder instance for method chaining.</returns>
    public RabbitMQMessageBusOptionsBuilder PublisherConfirmsEnabled(bool enabled = true)
    {
        Target.PublisherConfirmsEnabled = enabled;
        return this;
    }

    /// <summary>
    /// Sets the maximum time each publish attempt will wait for connection recovery before failing.
    /// During a transient connection drop, publishes suspend and resume automatically on recovery.
    /// Set to TimeSpan.Zero to disable waiting (fail immediately on connection drop).
    /// </summary>
    /// <param name="timeout">Maximum per-attempt recovery wait time. Default: 10 seconds.</param>
    /// <returns>The builder instance for method chaining.</returns>
    public RabbitMQMessageBusOptionsBuilder PublishRecoveryTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        Target.PublishRecoveryTimeout = timeout;
        return this;
    }

    /// <summary>
    /// Configures the message bus for RabbitMQ quorum queues using recommended settings.
    /// Disables auto-delete and exclusive mode for subscription queues,
    /// and sets quorum-specific arguments based on the current DeliveryLimit value.
    /// Preserves any existing arguments.
    /// </summary>
    /// <returns>The builder instance for method chaining.</returns>
    public RabbitMQMessageBusOptionsBuilder UseQuorumQueues()
    {
        Target.SubscriptionQueueAutoDelete = false;
        Target.IsSubscriptionQueueExclusive = false;

        Target.Arguments ??= new Dictionary<string, object?>();

        // Add or update quorum-specific arguments
        Target.Arguments["x-queue-type"] = "quorum";
        Target.Arguments["x-delivery-limit"] = Target.DeliveryLimit;

        return this;
    }

    /// <summary>
    /// Sets the heartbeat timeout negotiated with the broker.
    /// Controls how quickly dead TCP connections are detected.
    /// </summary>
    /// <param name="heartbeat">Heartbeat interval. TimeSpan.Zero disables heartbeats.</param>
    public RabbitMQMessageBusOptionsBuilder RequestedHeartbeat(TimeSpan heartbeat)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(heartbeat, TimeSpan.Zero);
        Target.RequestedHeartbeat = heartbeat;
        return this;
    }

    /// <summary>
    /// Sets the interval between automatic connection recovery attempts.
    /// </summary>
    /// <param name="interval">Recovery interval. Must be positive.</param>
    public RabbitMQMessageBusOptionsBuilder NetworkRecoveryInterval(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        Target.NetworkRecoveryInterval = interval;
        return this;
    }

    /// <summary>
    /// Configures a dead letter exchange for messages that exceed the delivery limit or are rejected.
    /// </summary>
    /// <param name="exchange">The DLX exchange name.</param>
    /// <param name="routingKey">Optional routing key for dead-lettered messages.</param>
    /// <param name="strategy">Dead-letter strategy. AtLeastOnce requires overflow to be RejectPublish.</param>
    public RabbitMQMessageBusOptionsBuilder DeadLetterExchange(string exchange, string? routingKey = null, DeadLetterStrategy? strategy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exchange);
        Target.DeadLetterExchange = exchange;
        Target.DeadLetterRoutingKey = routingKey;
        Target.DeadLetterStrategy = strategy;
        return this;
    }

    /// <summary>
    /// Sets the queue overflow behavior when max length is reached.
    /// Must be RejectPublish when using at-least-once dead-lettering on quorum queues.
    /// </summary>
    /// <param name="behavior">The overflow behavior.</param>
    public RabbitMQMessageBusOptionsBuilder OverflowBehavior(QueueOverflowBehavior behavior)
    {
        Target.Overflow = behavior;
        return this;
    }

    /// <summary>
    /// Sets the consumer timeout for quorum queues (RabbitMQ 4.3+).
    /// When a consumer holds unacknowledged messages longer than this, the broker returns them
    /// and gracefully cancels the consumer.
    /// </summary>
    /// <param name="timeout">Timeout duration. Must be positive.</param>
    public RabbitMQMessageBusOptionsBuilder ConsumerTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        Target.ConsumerTimeout = timeout;
        return this;
    }

    /// <summary>
    /// Enables single active consumer mode for strict message ordering with automatic failover.
    /// Only one consumer at a time will receive messages; others act as standby.
    /// </summary>
    /// <param name="enabled">Whether to enable single active consumer. Default: true.</param>
    public RabbitMQMessageBusOptionsBuilder UseSingleActiveConsumer(bool enabled = true)
    {
        Target.SingleActiveConsumer = enabled;
        return this;
    }

    /// <summary>
    /// Enables message priority on the queue. Messages published with higher priority are
    /// delivered to consumers before lower-priority messages.
    /// RabbitMQ 4.3+ quorum queues support up to 32 strict priority levels.
    /// </summary>
    /// <param name="maxPriority">Maximum priority levels (1-32). Default: 32.</param>
    public RabbitMQMessageBusOptionsBuilder UseMessagePriority(byte maxPriority = 32)
    {
        ArgumentOutOfRangeException.ThrowIfZero(maxPriority);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxPriority, (byte)32);

        Target.MaxPriority = maxPriority;
        return this;
    }

    /// <summary>
    /// Configures native delayed retry for quorum queues (RabbitMQ 4.3+).
    /// Rejected/failed messages are held in a delayed state with linear backoff before redelivery.
    /// This replaces the need for the delayed message exchange plugin for retry scenarios.
    /// </summary>
    /// <param name="minDelayMs">Minimum delay in milliseconds (multiplied by delivery count).</param>
    /// <param name="maxDelayMs">Maximum delay cap in milliseconds.</param>
    /// <param name="retryType">Retry type controlling which messages are delayed. Default: All.</param>
    public RabbitMQMessageBusOptionsBuilder UseDelayedRetries(int minDelayMs = 1000, int maxDelayMs = 60000, DelayedRetryType retryType = DelayedRetryType.All)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(minDelayMs, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxDelayMs, 0);

        if (maxDelayMs < minDelayMs)
            throw new ArgumentOutOfRangeException(nameof(maxDelayMs), $"maxDelayMs ({maxDelayMs}) must be >= minDelayMs ({minDelayMs})");

        Target.DelayedRetryType = retryType;
        Target.DelayedRetryMin = minDelayMs;
        Target.DelayedRetryMax = maxDelayMs;
        return this;
    }
}

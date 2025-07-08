using System;
using System.Collections.Generic;

namespace Foundatio.Messaging;

public class RabbitMQMessageBusOptions : SharedMessageBusOptions
{
    /// <summary>
    /// The connection string. See https://www.rabbitmq.com/uri-spec.html for more information.
    /// </summary>
    public string ConnectionString { get; set; }

    /// <summary>
    /// The default message time to live. The value of the expiration field describes the TTL period in milliseconds.
    /// </summary>
    public TimeSpan? DefaultMessageTimeToLive { get; set; }

    /// <summary>
    /// Arguments passed to QueueDeclare. Some brokers use it to implement additional features like message TTL.
    /// </summary>
    public IDictionary<string, object> Arguments { get; set; }

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
    public bool GlobalQos { get; set; }
}

public class RabbitMQMessageBusOptionsBuilder : SharedMessageBusOptionsBuilder<RabbitMQMessageBusOptions, RabbitMQMessageBusOptionsBuilder>
{
    public RabbitMQMessageBusOptionsBuilder ConnectionString(string connectionString)
    {
        Target.ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        return this;
    }

    public RabbitMQMessageBusOptionsBuilder DefaultMessageTimeToLive(TimeSpan defaultMessageTimeToLive)
    {
        Target.DefaultMessageTimeToLive = defaultMessageTimeToLive;
        return this;
    }

    public RabbitMQMessageBusOptionsBuilder Arguments(IDictionary<string, object> arguments)
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

    public RabbitMQMessageBusOptionsBuilder GlobalQos(bool globalQos)
    {
        Target.GlobalQos = globalQos;
        return this;
    }
}

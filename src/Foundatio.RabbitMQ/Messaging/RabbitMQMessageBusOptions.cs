using System;
using System.Collections.Generic;

namespace Foundatio.Messaging
{
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
        /// Whether or not the subscription queue is exclusive to this message bus instance.
        /// </summary>
        public bool IsSubscriptionQueueExclusive { get; set; } = true;

        /// <summary>
        /// Whether or not the subscription queue should be automatically deleted.
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
    }
}

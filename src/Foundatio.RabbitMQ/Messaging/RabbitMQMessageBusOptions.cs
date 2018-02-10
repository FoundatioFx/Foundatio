using System;
using System.Collections.Generic;

namespace Foundatio.Messaging {
    public class RabbitMQMessageBusOptions : SharedMessageBusOptions {
        /// <summary>
        /// The connection string. See https://www.rabbitmq.com/uri-spec.html for more information.
        /// </summary>
        public string ConnectionString { get; set; }

        /// <summary>
        /// Name of the direct exchange that delivers messages to queues based on a message routing key.
        /// </summary>
        public string ExchangeName { get; set; } = "default";

        /// <summary>
        /// The default message time to live. The value of the expiration field describes the TTL period in milliseconds.
        /// </summary>
        public TimeSpan? DefaultMessageTimeToLive { get; set; }

        /// <summary>
        /// Arguments passed to QueueDeclare. Some brokers use it to implement additional features like message TTL.
        /// </summary>
        public IDictionary<string, object> Arguments { get; set; }

        /// <summary>
        /// Durable (the queue will survive a broker restart)
        /// </summary>
        public bool IsQueueDurable { get; set; } = true;

        /// <summary>
        /// Exclusive (used by only one connection and the queue will be deleted when that connection closes)
        /// </summary>
        public bool IsQueueExclusive { get; set; }

        /// <summary>
        /// Auto-delete (queue is deleted when last consumer unsubscribes)
        /// </summary>
        public bool IsQueueAutoDeleteEnabled { get; set; } = true;

        /// <summary>
        /// Durable (the exchange will survive a broker restart)
        /// </summary>
        public bool IsExchangeDurable { get; set; } = true;

        /// <summary>
        /// Exclusive (used by only one connection and the exchange will be deleted when that connection closes)
        /// </summary>
        public bool IsExchangeExclusive { get; set; }
    }

    public class RabbitMQMessageBusOptionsBuilder : SharedMessageBusOptionsBuilder<RabbitMQMessageBusOptions, RabbitMQMessageBusOptionsBuilder> {
        public RabbitMQMessageBusOptionsBuilder ConnectionString(string connectionString) {
            Target.ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            return this;
        }

        public RabbitMQMessageBusOptionsBuilder ExchangeName(string exchangeName) {
            Target.ExchangeName = exchangeName ?? throw new ArgumentNullException(nameof(exchangeName));
            return this;
        }

        public RabbitMQMessageBusOptionsBuilder DefaultMessageTimeToLive(TimeSpan defaultMessageTimeToLive) {
            Target.DefaultMessageTimeToLive = defaultMessageTimeToLive;
            return this;
        }

        public RabbitMQMessageBusOptionsBuilder Arguments(IDictionary<string, object> arguments) {
            Target.Arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
            return this;
        }

        public RabbitMQMessageBusOptionsBuilder IsQueueDurable(bool isQueueDurable) {
            Target.IsQueueDurable = isQueueDurable;
            return this;
        }

        public RabbitMQMessageBusOptionsBuilder IsQueueExclusive(bool isQueueExclusive) {
            Target.IsQueueExclusive = isQueueExclusive;
            return this;
        }

        public RabbitMQMessageBusOptionsBuilder IsQueueAutoDeleteEnabled(bool isQueueAutoDeleteEnabled) {
            Target.IsQueueAutoDeleteEnabled = isQueueAutoDeleteEnabled;
            return this;
        }

        public RabbitMQMessageBusOptionsBuilder IsExchangeDurable(bool isExchangeDurable) {
            Target.IsExchangeDurable = isExchangeDurable;
            return this;
        }

        public RabbitMQMessageBusOptionsBuilder IsExchangeExclusive(bool isExchangeExclusive) {
            Target.IsExchangeExclusive = isExchangeExclusive;
            return this;
        }
    }
}
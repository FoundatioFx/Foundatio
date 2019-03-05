using System;
using System.Collections.Generic;

namespace Foundatio.Messaging {
    public class RabbitMQMessageBusOptions : SharedMessageBusOptions {
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
    }

    public class RabbitMQMessageBusOptionsBuilder : SharedMessageBusOptionsBuilder<RabbitMQMessageBusOptions, RabbitMQMessageBusOptionsBuilder> {
        public RabbitMQMessageBusOptionsBuilder ConnectionString(string connectionString) {
            Target.ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
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

        public RabbitMQMessageBusOptionsBuilder IsDurable(bool isDurable) {
            Target.IsDurable = isDurable;
            return this;
        }
    }
}
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Serializer;
using Nito.AsyncEx;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace Foundatio.Messaging {
    public class RabbitMQMessageBusOptions : MesssageBusOptions {
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
    }

    public class RabbitMQMessageBus : MessageBusBase<RabbitMQMessageBusOptions> {
        private readonly AsyncLock _lock = new AsyncLock();
        private readonly ConnectionFactory _factory;
        private IConnection _publisherClient;
        private IConnection _subscriberClient;
        private IModel _publisherChannel;
        private IModel _subscriberChannel;
        private bool _delayedExchangePluginEnabled = true;

        /// <summary>
        /// Exchange type set as fanout exchange that uses the exchange name
        /// </summary>
        /// <param name="connectionString">The connection string. See https://www.rabbitmq.com/uri-spec.html for more information.</param>
        /// <param name="queueName">Name of the queue established by the subscriber when they call QueueDeclare. Its not used by publisher.</param>
        /// <param name="exhangeName">Name of the direct exchange that delivers messages to queues based on a message routing key</param>
        /// <param name="queueArguments">queue arguments</param>
        /// <param name="defaultMessageTimeToLive">The value of the expiration field describes the TTL period in milliseconds</param>
        /// <param name="serializer">For data serialization</param>
        /// <param name="loggerFactory">logger</param>
        /// <remarks>https://www.rabbitmq.com/dotnet-api-guide.html#connection-recovery</remarks>
        [Obsolete("Use the options overload")]
        public RabbitMQMessageBus(string connectionString, string queueName, string exhangeName, IDictionary<string, object> queueArguments = null, TimeSpan? defaultMessageTimeToLive = null, ISerializer serializer = null, ILoggerFactory loggerFactory = null) : this(new RabbitMQMessageBusOptions { ConnectionString = connectionString, Topic = queueName, ExchangeName = exhangeName, DefaultMessageTimeToLive = defaultMessageTimeToLive, Arguments = queueArguments, Serializer = serializer, LoggerFactory = loggerFactory }) { }

        public RabbitMQMessageBus(RabbitMQMessageBusOptions options) : base(options) {
            if (String.IsNullOrEmpty(options.ConnectionString))
                throw new ArgumentException("ConnectionString is required.");

            // Initialize the connection factory. automatic recovery will allow the connections to be restored
            // in case the server is restarted or there has been any network failures
            // Topology ( queues, exchanges, bindings and consumers) recovery "TopologyRecoveryEnabled" is already enabled
            // by default so no need to initialize it. NetworkRecoveryInterval is also by default set to 5 seconds.
            // it can always be fine tuned if needed.
            _factory = new ConnectionFactory { Uri = options.ConnectionString, AutomaticRecoveryEnabled = true };
        }

        protected override async Task EnsureTopicSubscriptionAsync(CancellationToken cancellationToken) {
            if (_subscriberChannel != null)
                return;

            await EnsureTopicCreatedAsync(cancellationToken).AnyContext();

            using (await _lock.LockAsync().AnyContext()) {
                if (_subscriberChannel != null)
                    return;

                _subscriberClient = CreateConnection();
                _subscriberChannel = _subscriberClient.CreateModel();

                // If InitPublisher is called first, then we will never come in this if clause.
                if (!CreateDelayedExchange(_subscriberChannel)) {
                    _subscriberClient = CreateConnection();
                    _subscriberChannel = _subscriberClient.CreateModel();
                    CreateRegularExchange(_subscriberChannel);
                }

                CreateQueue(_subscriberChannel);
                var consumer = new EventingBasicConsumer(_subscriberChannel);
                consumer.Received += OnMessageAsync;
                consumer.Shutdown += OnConsumerShutdown;

                _subscriberChannel.BasicConsume(_options.Topic, true, consumer);
                _logger.Trace("The unique channel number for the subscriber is : {channelNumber}", _subscriberChannel.ChannelNumber);
            }
        }

        private void OnConsumerShutdown(object sender, ShutdownEventArgs e) {
            _logger.Info(() => $"Consumer shutdown. Reply Code: {e.ReplyCode} Reason: {e.ReplyText}");
        }

        private async void OnMessageAsync(object sender, BasicDeliverEventArgs e) {
            if (_subscribers.IsEmpty)
                return;

            _logger.Trace("OnMessageAsync({messageId})", e.BasicProperties?.MessageId);
            MessageBusData message;
            try {
                message = await _serializer.DeserializeAsync<MessageBusData>(e.Body).AnyContext();
            } catch (Exception ex) {
                _logger.Warn(ex, "OnMessageAsync({0}) Error deserializing messsage: {1}", e.BasicProperties?.MessageId, ex.Message);
                return;
            }

            await SendMessageToSubscribersAsync(message, _serializer).AnyContext();
        }

        protected override async Task EnsureTopicCreatedAsync(CancellationToken cancellationToken) {
            if (_publisherChannel != null)
                return;

            using (await _lock.LockAsync().AnyContext()) {
                if (_publisherChannel != null)
                    return;

                // Create the client connection, channel, declares the exchange, queue and binds
                // the exchange with the publisher queue. It requires the name of our exchange, exhange type, durability and autodelete.
                // For now we are using same autoDelete for both exchange and queue ( it will survive a server restart )
                _publisherClient = CreateConnection();
                _publisherChannel = _publisherClient.CreateModel();

                // We first attempt to create "x-delayed-type". For this plugin should be installed.
                // However, we plugin is not installed this will throw an exception. In that case
                // we attempt to create regular exchange. If regular exchange also throws and exception
                // then trouble shoot the problem.
                if (!CreateDelayedExchange(_publisherChannel)) {
                    // if the initial exchange creation was not successful then we must close the previous connection
                    // and establish the new client connection and model otherwise you will keep recieving failure in creation
                    // of the regular exchange too.
                    _publisherClient = CreateConnection();
                    _publisherChannel = _publisherClient.CreateModel();
                    CreateRegularExchange(_publisherChannel);
                }

                _logger.Trace("The unique channel number for the publisher is : {channelNumber}", _publisherChannel.ChannelNumber);
            }
        }

        /// <summary>
        /// Publish the message
        /// </summary>
        /// <param name="messageType"></param>
        /// <param name="message"></param>
        /// <param name="delay">Along with the delay value, _delayExchange should also be set to true</param>
        /// <param name="cancellationToken"></param>
        /// <remarks>RabbitMQ has an upper limit of 2GB for messages.BasicPublish blocking AMQP operations.
        /// The rule of thumb is: avoid sharing channels across threads.
        /// Publishers in your application that publish from separate threads should use their own channels.
        /// The same is a good idea for consumers.</remarks>
        protected override async Task PublishImplAsync(Type messageType, object message, TimeSpan? delay, CancellationToken cancellationToken) {
            var data = await _serializer.SerializeAsync(new MessageBusData {
                Type = messageType.AssemblyQualifiedName,
                Data = await _serializer.SerializeToStringAsync(message).AnyContext()
            }).AnyContext();

            // if the rabbitmq plugin is not availaible then use the base class delay mechanism
            if (!_delayedExchangePluginEnabled && delay.HasValue && delay.Value > TimeSpan.Zero) {
                _logger.Trace("Schedule delayed message: {messageType} ({delay}ms)", messageType.FullName, delay.Value.TotalMilliseconds);
                await AddDelayedMessageAsync(messageType, message, delay.Value).AnyContext();
                return;
            }

            var basicProperties = _publisherChannel.CreateBasicProperties();
            if (_options.DefaultMessageTimeToLive.HasValue)
                basicProperties.Expiration = _options.DefaultMessageTimeToLive.Value.TotalMilliseconds.ToString(CultureInfo.InvariantCulture);

            // RabbitMQ only supports delayed messages with a third party plugin called "rabbitmq_delayed_message_exchange"
            if (_delayedExchangePluginEnabled && delay.HasValue && delay.Value > TimeSpan.Zero) {
                // Its necessary to typecast long to int because rabbitmq on the consumer side is reading the
                // data back as signed (using BinaryReader#ReadInt64). You will see the value to be negative
                // and the data will be delievered immediately.
                basicProperties.Headers = new Dictionary<string, object> { { "x-delay", Convert.ToInt32(delay.Value.TotalMilliseconds) } };

                _logger.Trace("Schedule delayed message: {messageType} ({delay}ms)", messageType.FullName, delay.Value.TotalMilliseconds);
            } else {
                _logger.Trace("Message Publish: {messageType}", messageType.FullName);
            }

            // The publication occurs with mandatory=false
            _publisherChannel.BasicPublish(_options.ExchangeName, String.Empty, basicProperties, data);
        }

        /// <summary>
        /// Connect to a broker - RabbitMQ
        /// </summary>
        /// <returns></returns>
        private IConnection CreateConnection() {
            return _factory.CreateConnection();
        }

        /// <summary>
        /// Attempts to create the delayed exchange.
        /// </summary>
        /// <param name="model"></param>
        /// <returns>true if the delayed exchange was successfully declared. Which means plugin was installed.</returns>
        private bool CreateDelayedExchange(IModel model) {
            bool success = true;
            if (!_delayedExchangePluginEnabled)
                return true;

            try {
                // This exchange is a delayed exchange (direct). You need rabbitmq_delayed_message_exchange plugin to RabbitMQ
                // Disclaimer : https://github.com/rabbitmq/rabbitmq-delayed-message-exchange/ . Please read the *Performance
                // Impact* of the delayed exchange type.
                var args = new Dictionary<string, object> { { "x-delayed-type", ExchangeType.Fanout } };
                model.ExchangeDeclare(_options.ExchangeName, "x-delayed-message", true, false, args);
            } catch (OperationInterruptedException o) {
                if (o.ShutdownReason.ReplyCode == 503) {
                    _delayedExchangePluginEnabled = false;
                    success = false;
                    _logger.Info(o, "Not able to create x-delayed-type exchange");
                }
            }

            return success;
        }

        private void CreateRegularExchange(IModel model) {
            model.ExchangeDeclare(_options.ExchangeName, ExchangeType.Fanout, true, false, null);
        }

        /// <summary>
        /// The client sends a message to an exchange and attaches a routing key to it.
        /// The message is sent to all queues with the matching routing key. Each queue has a
        /// receiver attached which will process the message. We’ll initiate a dedicated message
        /// exchange and not use the default one. Note that a queue can be dedicated to one or more routing keys.
        /// </summary>
        /// <param name="model">channel</param>
        private void CreateQueue(IModel model) {
            // Setup the queue where the messages will reside - it requires the queue name and durability.
            // Durable (the queue will survive a broker restart)
            // Exclusive (used by only one connection and the queue will be deleted when that connection closes)
            // Auto-delete (queue is deleted when last consumer unsubscribes)
            // Arguments (some brokers use it to implement additional features like message TTL)
            model.QueueDeclare(_options.Topic, /*durable*/ true, /*exclusive*/ false, /*autodelete*/ true, _options.Arguments);

            // bind the queue with the exchange.
            model.QueueBind(_options.Topic, _options.ExchangeName, "");
        }

        public override void Dispose() {
            base.Dispose();
            ClosePublisherConnection();
            CloseSubscriberConnection();
        }

        private void ClosePublisherConnection() {
            if (_publisherClient == null)
                return;

            using (_lock.Lock()) {
                if (_publisherClient == null)
                    return;

                if (_publisherChannel != null &&_publisherChannel.IsOpen)
                    _publisherChannel.Close();

                _publisherChannel?.Dispose();
                _publisherChannel = null;

                if (_publisherClient != null && _publisherClient.IsOpen)
                    _publisherClient.Close();

                _publisherClient?.Dispose();
                _publisherClient = null;
            }
        }

        private void CloseSubscriberConnection() {
            if (_subscriberClient == null)
                return;

            using (_lock.Lock()) {
                if (_subscriberClient == null)
                    return;

                if (_subscriberChannel != null && _subscriberChannel.IsOpen)
                    _subscriberChannel.Close();

                _subscriberChannel?.Dispose();
                _subscriberChannel = null;

                if (_subscriberClient != null && _subscriberClient.IsOpen)
                    _subscriberClient.Close();

                _subscriberClient?.Dispose();
                _subscriberClient = null;
            }
        }
    }
}
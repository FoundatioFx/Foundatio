using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Serializer;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Foundatio.Messaging {
    public class RabbitMQMessageBus : MessageBusBase, IMessageBus {
        private readonly string _queueName;
        private readonly string _routingKey;
        private readonly string _exchangeName;
        private readonly bool _durable;
        private readonly bool _persistent;
        private readonly bool _exclusive;
        private readonly bool _autoDelete;
        private readonly IDictionary<string, object> _queueArguments;
        private readonly ISerializer _serializer;
        private readonly IConnectionFactory _factory;
        private readonly IConnection _publisherClient;
        private readonly IConnection _subscriberClient;
        private readonly IModel _publisherChannel;
        private readonly IModel _subscriberChannel;
        private readonly TimeSpan _defaultMessageTimeToLive = TimeSpan.MaxValue;

        /// <summary>
        /// Constructor for RabbitMqMessaging - Exchange type set as Direct exchange that uses the routing key
        /// </summary>
        /// <param name="userName">username needed to create the connection with the broker</param>
        /// <param name="password">password needed to create the connection with the broker</param>
        /// <param name="queueName">Queue name</param>
        /// <param name="routingKey">The routing key is an "address" that the exchange may use to decide how to route the message</param>
        /// <param name="exhangeName">Name of the direct exchange that delivers messages to queues based on a message routing key</param>
        /// <param name="durable">Durable exchanges survive broker restart</param>
        /// <param name="autoDelete">True, if you want the queue to be deleted when the connection is closed</param>
        /// <param name="queueArguments">queue arguments</param>
        /// <param name="defaultMessageTimeToLive">The value of the expiration field describes the TTL period in milliseconds</param>
        /// <param name="serializer">For data serialization</param>
        /// <param name="loggerFactory">logger</param>
        /// <param name="persistent">When set to true, RabbitMQ will persist message to disk</param>
        /// <param name="exclusive"></param>
        public RabbitMQMessageBus(string userName, string password, string queueName, string routingKey, string exhangeName, bool durable, bool persistent, bool exclusive, bool autoDelete, IDictionary<string, object> queueArguments = null, TimeSpan? defaultMessageTimeToLive = null, ISerializer serializer = null, ILoggerFactory loggerFactory = null) : base(loggerFactory) {
            _serializer = serializer ?? new JsonNetSerializer();
            _exchangeName = exhangeName;
            _queueName = queueName;
            _routingKey = routingKey;
            _durable = durable;
            _persistent = persistent;
            _exclusive = exclusive;
            _autoDelete = autoDelete;
            _queueArguments = queueArguments;

            if (defaultMessageTimeToLive.HasValue && defaultMessageTimeToLive.Value > TimeSpan.Zero)
                _defaultMessageTimeToLive = defaultMessageTimeToLive.Value;

            // initialize connection factory
            _factory = new ConnectionFactory {
                UserName = userName,
                Password = password
            };

            // initialize publisher
            _publisherClient = CreateConnection();
            _publisherChannel = _publisherClient.CreateModel();
            SetUpExchangeAndQueuesForRouting(_publisherChannel);
            _logger.Trace("The unique channel number for the publisher is : {channelNumber}", _publisherChannel.ChannelNumber);

            // initialize subscriber
            _subscriberClient = CreateConnection();
            _subscriberChannel = _subscriberClient.CreateModel();
            SetUpExchangeAndQueuesForRouting(_subscriberChannel);
            _logger.Trace("The unique channel number for the subscriber is : {channelNumber}", _subscriberChannel.ChannelNumber);
        }

        /// <summary>
        /// The client sends a message to an exchange and attaches a routing key to it. 
        /// The message is sent to all queues with the matching routing key. Each queue has a
        /// receiver attached which will process the message. We’ll initiate a dedicated message
        /// exchange and not use the default one. Note that a queue can be dedicated to one or more routing keys.
        /// </summary>
        /// <param name="model">channel</param>
        private void SetUpExchangeAndQueuesForRouting(IModel model) {
            // setup the message router - it requires the name of our exchange, exhange type and durability
            // ( it will survive a server restart )
            model.ExchangeDeclare(_exchangeName, ExchangeType.Direct, _durable);
            // setup the queue where the messages will reside - it requires the queue name and durability.
            model.QueueDeclare(_queueName, _durable, _exclusive, _autoDelete, _queueArguments);
            // bind the queue with the exchange.
            model.QueueBind(_queueName, _exchangeName, _routingKey);
        }

        /// <summary>
        /// Connect to a broker - RabbitMQ
        /// </summary>
        /// <returns></returns>
        private IConnection CreateConnection() {
            return _factory.CreateConnection();
        }

        /// <summary>
        /// Publish the message
        /// </summary>
        /// <param name="messageType"></param>
        /// <param name="message"></param>
        /// <param name="delay"></param>
        /// <param name="cancellationToken"></param>
        /// <remarks>RabbitMQ has an upper limit of 2GB for messages.BasicPublish blocking AMQP operations.
        /// The rule of thumb is: avoid sharing channels across threads.
        /// Publishers in your application that publish from separate threads should use their own channels.
        /// The same is a good idea for consumers.</remarks>
        /// <returns></returns>
        public override async Task PublishAsync(Type messageType, object message, TimeSpan? delay = null, CancellationToken cancellationToken = default(CancellationToken)) {
            if (message == null)
                return;
            _logger.Trace("Message Publish: {messageType}", messageType.FullName);

            // RabbitMQ only supports delayed messages with a third party plugin.
            if (delay.HasValue && delay.Value > TimeSpan.Zero) {
                await AddDelayedMessageAsync(messageType, message, delay.Value).AnyContext();
                return;
            }

            var data = await _serializer.SerializeAsync(new MessageBusData {
                Type = messageType.AssemblyQualifiedName,
                Data = await _serializer.SerializeToStringAsync(message).AnyContext()
            }).AnyContext();
            
            var basicProperties = _publisherChannel.CreateBasicProperties();
            basicProperties.Persistent = _persistent;
            basicProperties.Expiration = _defaultMessageTimeToLive.Milliseconds.ToString();

            // The publication occurs with mandatory=false
            _publisherChannel.BasicPublish(_exchangeName, _routingKey, basicProperties, data);
        }
        
        /// <summary>
        /// Subscribe for the message
        /// </summary>
        /// <typeparam name="T">Type of the subscriber who wants to be notified of the callback</typeparam>
        /// <param name="handler">callback handler</param>
        /// <param name="cancellationToken"></param>
        public override void Subscribe<T>(Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken = default(CancellationToken)) {
            var consumer = new EventingBasicConsumer(_subscriberChannel);
            consumer.Received += OnMessageAsync;

            _subscriberChannel.BasicConsume(_queueName, true, consumer);
            base.Subscribe(handler, cancellationToken);
        }

        private async void OnMessageAsync(object sender, BasicDeliverEventArgs e) {
            _logger.Trace("OnMessage: {messageId}", e.BasicProperties?.MessageId);
            var message = await _serializer.DeserializeAsync<MessageBusData>(e.Body).AnyContext();

            Type messageType;
            try {
                messageType = Type.GetType(message.Type);
            } catch (Exception ex) {
                _logger.Error(ex, "Error getting message body type: {0}", ex.Message);
                return;
            }

            object body = await _serializer.DeserializeAsync(message.Data, messageType).AnyContext();
            await SendMessageToSubscribersAsync(messageType, body).AnyContext();
        }
        
        public override void Dispose() {
            base.Dispose();
            CloseConnection();
        }

        private void CloseConnection() {
            if (_subscriberChannel.IsOpen)
                _subscriberChannel.Close();
            _subscriberChannel.Dispose();

            if (_subscriberClient.IsOpen)
                _subscriberClient.Close();
            _subscriberClient.Dispose();

            if (_publisherChannel.IsOpen)
                _publisherChannel.Close();
            _publisherChannel.Dispose();

            if (_publisherClient.IsOpen)
                _publisherClient.Close();
            _publisherClient.Dispose();
        }
    }
}
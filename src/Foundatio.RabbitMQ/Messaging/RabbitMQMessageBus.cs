using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
using Foundatio.Extensions;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace Foundatio.Messaging;

public class RabbitMQMessageBus : MessageBusBase<RabbitMQMessageBusOptions>, IAsyncDisposable
{
    private readonly AsyncLock _lock = new();
    private readonly ConnectionFactory _factory;
    private IConnection _publisherConnection;
    private IConnection _subscriberConnection;
    private IChannel _publisherChannel;
    private IChannel _subscriberChannel;
    private bool? _delayedExchangePluginEnabled;
    private bool _isDisposed;

    public RabbitMQMessageBus(RabbitMQMessageBusOptions options) : base(options)
    {
        if (String.IsNullOrEmpty(options.ConnectionString))
            throw new ArgumentException("ConnectionString is required.");

        // Initialize the connection factory. automatic recovery will allow the connections to be restored
        // in case the server is restarted or there has been any network failures
        // Topology ( queues, exchanges, bindings and consumers) recovery "TopologyRecoveryEnabled" is already enabled
        // by default so no need to initialize it. NetworkRecoveryInterval is also by default set to 5 seconds.
        // it can always be fine-tuned if needed.
        _factory = new ConnectionFactory
        {
            Uri = new Uri(options.ConnectionString),
            AutomaticRecoveryEnabled = true
        };
    }

    public RabbitMQMessageBus(Builder<RabbitMQMessageBusOptionsBuilder, RabbitMQMessageBusOptions> config)
        : this(config(new RabbitMQMessageBusOptionsBuilder()).Build()) { }

    protected override Task RemoveTopicSubscriptionAsync()
    {
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("RemoveTopicSubscriptionAsync");

        return CloseSubscriberConnectionAsync();
    }

    protected override async Task EnsureTopicSubscriptionAsync(CancellationToken cancellationToken)
    {
        if (_subscriberChannel != null)
            return;

        await EnsureTopicCreatedAsync(cancellationToken).AnyContext();

        using (await _lock.LockAsync().AnyContext())
        {
            if (_subscriberChannel != null)
                return;

            _subscriberConnection = await CreateConnectionAsync().AnyContext();
            _subscriberChannel = await _subscriberConnection.CreateChannelAsync(cancellationToken: cancellationToken).AnyContext();

            // If InitPublisher is called first, then we will never come in this if-clause.
            if (!await CreateDelayedExchangeAsync(_subscriberChannel).AnyContext())
            {
                await _subscriberChannel.DisposeAsync().AnyContext();
                await _subscriberConnection.DisposeAsync().AnyContext();

                _subscriberConnection = await CreateConnectionAsync().AnyContext();
                _subscriberChannel = await _subscriberConnection.CreateChannelAsync(cancellationToken: cancellationToken).AnyContext();
                await CreateRegularExchangeAsync(_subscriberChannel).AnyContext();
            }

            string queueName = await CreateQueueAsync(_subscriberChannel).AnyContext();
            var consumer = new AsyncEventingBasicConsumer(_subscriberChannel);
            consumer.ReceivedAsync += OnMessageAsync;
            consumer.ShutdownAsync += OnConsumerShutdownAsync;

            await _subscriberChannel.BasicConsumeAsync(queueName, _options.AcknowledgementStrategy == AcknowledgementStrategy.FireAndForget, consumer, cancellationToken: cancellationToken).AnyContext();
            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("The unique channel number for the subscriber is : {ChannelNumber}", _subscriberChannel.ChannelNumber);
        }
    }

    private Task OnConsumerShutdownAsync(object sender, ShutdownEventArgs e)
    {
        _logger.LogInformation("Consumer shutdown. Reply Code: {ReplyCode} Reason: {ReplyText}", e.ReplyCode, e.ReplyText);
        return Task.CompletedTask;
    }

    private async Task OnMessageAsync(object sender, BasicDeliverEventArgs envelope)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("OnMessageAsync({MessageId})", envelope.BasicProperties?.MessageId);

        if (_subscribers.IsEmpty)
        {
            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("No subscribers ({MessageId})", envelope.BasicProperties?.MessageId);

            if (_options.AcknowledgementStrategy == AcknowledgementStrategy.Automatic)
                await _subscriberChannel.BasicRejectAsync(envelope.DeliveryTag, true).AnyContext();

            return;
        }

        try
        {
            var message = ConvertToMessage(envelope);
            await SendMessageToSubscribersAsync(message).AnyContext();

            if (_options.AcknowledgementStrategy == AcknowledgementStrategy.Automatic)
                await _subscriberChannel.BasicAckAsync(envelope.DeliveryTag, false).AnyContext();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling message ({MessageId}): {Message}", envelope.BasicProperties?.MessageId, ex.Message);

            if (_options.AcknowledgementStrategy == AcknowledgementStrategy.Automatic)
                await _subscriberChannel.BasicRejectAsync(envelope.DeliveryTag, true).AnyContext();
        }
    }

    /// <summary>
    /// Get MessageBusData from a RabbitMQ delivery
    /// </summary>
    /// <param name="envelope">The RabbitMQ delivery arguments</param>
    /// <returns>The MessageBusData for the message</returns>
    protected virtual IMessage ConvertToMessage(BasicDeliverEventArgs envelope)
    {
        var message = new Message(envelope.Body.ToArray(), DeserializeMessageBody)
        {
            Type = envelope.BasicProperties.Type,
            ClrType = GetMappedMessageType(envelope.BasicProperties.Type),
            CorrelationId = envelope.BasicProperties.CorrelationId,
            UniqueId = envelope.BasicProperties.MessageId
        };

        if (envelope.BasicProperties.Headers != null)
            foreach (var header in envelope.BasicProperties.Headers)
            {
                if (header.Value is byte[] byteData)
                    message.Properties[header.Key] = Encoding.UTF8.GetString(byteData);
                else
                    message.Properties[header.Key] = header.Value.ToString();
            }

        return message;
    }

    protected override async Task EnsureTopicCreatedAsync(CancellationToken cancellationToken)
    {
        if (_publisherChannel != null)
            return;

        using (await _lock.LockAsync().AnyContext())
        {
            if (_publisherChannel != null)
                return;

            // Create the client connection, channel, declares the exchange, queue and binds
            // the exchange with the publisher queue. It requires the name of our exchange, exchange type, durability and auto-delete.
            // For now, we are using same autoDelete for both exchange and queue ( it will survive a server restart )
            _publisherConnection = await CreateConnectionAsync().AnyContext();
            _publisherChannel = await _publisherConnection.CreateChannelAsync(cancellationToken: cancellationToken).AnyContext();

            // We first attempt to create "x-delayed-type". For this plugin should be installed.
            // However, we plug in is not installed this will throw an exception. In that case
            // we attempt to create regular exchange. If regular exchange also throws and exception
            // then trouble shoot the problem.
            if (!await CreateDelayedExchangeAsync(_publisherChannel).AnyContext())
            {
                // if the initial exchange creation was not successful then we must close the previous connection
                // and establish the new client connection and model otherwise you will keep receiving failure in creation
                // of the regular exchange too.
                await _publisherChannel.DisposeAsync().AnyContext();
                await _publisherConnection.DisposeAsync().AnyContext();

                _publisherConnection = await CreateConnectionAsync().AnyContext();
                _publisherChannel = await _publisherConnection.CreateChannelAsync(cancellationToken: cancellationToken).AnyContext();
                await CreateRegularExchangeAsync(_publisherChannel).AnyContext();
            }

            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("The unique channel number for the publisher is : {ChannelNumber}", _publisherChannel.ChannelNumber);
        }
    }

    /// <summary>
    /// Publish the message
    /// </summary>
    /// <param name="messageType"></param>
    /// <param name="message"></param>
    /// <param name="options">Message options</param>
    /// <param name="cancellationToken"></param>
    /// <remarks>RabbitMQ has an upper limit of 2GB for messages.BasicPublish blocking AMQP operations.
    /// The rule of thumb is: avoid sharing channels across threads.
    /// Publishers in your application that publish from separate threads should use their own channels.
    /// The same is a good idea for consumers.</remarks>
    protected override async Task PublishImplAsync(string messageType, object message, MessageOptions options, CancellationToken cancellationToken)
    {
        byte[] data = SerializeMessageBody(messageType, message);

        // if the RabbitMQ plugin is not available then use the base class delay mechanism
        if (!_delayedExchangePluginEnabled.Value && options.DeliveryDelay.HasValue && options.DeliveryDelay.Value > TimeSpan.Zero)
        {
            var mappedType = GetMappedMessageType(messageType);
            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("Schedule delayed message: {MessageType} ({Delay}ms)", messageType, options.DeliveryDelay.Value.TotalMilliseconds);

            await AddDelayedMessageAsync(mappedType, message, options.DeliveryDelay.Value).AnyContext();
            return;
        }

        var basicProperties = new BasicProperties
        {
            MessageId = options.UniqueId ?? Guid.NewGuid().ToString("N"),
            CorrelationId = options.CorrelationId,
            Type = messageType,
        };

        if (_options.IsDurable)
            basicProperties.Persistent = true;
        if (_options.DefaultMessageTimeToLive.HasValue)
            basicProperties.Expiration = _options.DefaultMessageTimeToLive.Value.TotalMilliseconds.ToString(CultureInfo.InvariantCulture);

        if (options.Properties.Count > 0)
        {
            basicProperties.Headers ??= new Dictionary<string, object>();
            foreach (var property in options.Properties)
                basicProperties.Headers.Add(property.Key, property.Value);
        }

        // RabbitMQ only supports delayed messages with a third party plugin called "rabbitmq_delayed_message_exchange"
        if (_delayedExchangePluginEnabled.Value && options.DeliveryDelay.HasValue && options.DeliveryDelay.Value > TimeSpan.Zero)
        {
            // It's necessary to typecast long to int because RabbitMQ on the consumer side is reading the
            // data back as signed (using BinaryReader#ReadInt64). You will see the value to be negative
            // and the data will be delivered immediately.
            basicProperties.Headers = new Dictionary<string, object> { { "x-delay", Convert.ToInt32(options.DeliveryDelay.Value.TotalMilliseconds) } };

            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Schedule delayed message: {MessageType} ({Delay}ms)", messageType, options.DeliveryDelay.Value.TotalMilliseconds);
        }
        else
        {
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Message publish type {MessageType} {MessageId}", messageType, basicProperties.MessageId);
        }

        using (await _lock.LockAsync().AnyContext())
            await _publisherChannel.BasicPublishAsync<BasicProperties>(_options.Topic, String.Empty, mandatory: false, basicProperties, data, cancellationToken: cancellationToken).AnyContext();

        if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Done publishing type {MessageType} {MessageId}", messageType, basicProperties.MessageId);
    }

    /// <summary>
    /// Connect to a broker - RabbitMQ
    /// </summary>
    /// <returns></returns>
    private Task<IConnection> CreateConnectionAsync()
    {
        return _factory.CreateConnectionAsync();
    }

    /// <summary>
    /// Attempts to create the delayed exchange.
    /// </summary>
    /// <param name="channel"></param>
    /// <returns>true if the delayed exchange was successfully declared. Which means plugin was installed.</returns>
    private async Task<bool> CreateDelayedExchangeAsync(IChannel channel)
    {
        bool success = true;
        if (_delayedExchangePluginEnabled.HasValue)
            return _delayedExchangePluginEnabled.Value;

        try
        {
            // This exchange is a delayed exchange (fanout). You need rabbitmq_delayed_message_exchange plugin to RabbitMQ
            // Disclaimer : https://github.com/rabbitmq/rabbitmq-delayed-message-exchange/
            // Please read the *Performance Impact* of the delayed exchange type.
            var args = new Dictionary<string, object> { { "x-delayed-type", ExchangeType.Fanout } };
            await channel.ExchangeDeclareAsync(_options.Topic, "x-delayed-message", _options.IsDurable, false, args).AnyContext();
        }
        catch (OperationInterruptedException ex)
        {
            _logger.LogInformation(ex, "Unable to create x-delayed-type exchange: {Message}", ex.Message);
            success = false;
        }

        _delayedExchangePluginEnabled = success;
        return _delayedExchangePluginEnabled.Value;
    }

    private Task CreateRegularExchangeAsync(IChannel channel)
    {
        return channel.ExchangeDeclareAsync(_options.Topic, ExchangeType.Fanout, _options.IsDurable, false);
    }

    /// <summary>
    /// The client sends a message to an exchange and attaches a routing key to it.
    /// The message is sent to all queues with the matching routing key. Each queue has a
    /// receiver attached which will process the message. We’ll initiate a dedicated message
    /// exchange and not use the default one. Note that a queue can be dedicated to one or more routing keys.
    /// </summary>
    /// <param name="channel">channel</param>
    private async Task<string> CreateQueueAsync(IChannel channel)
    {
        // Set up the queue where the messages will reside - it requires the queue name and durability.
        // Durable (the queue will survive a broker restart)
        // Arguments (some brokers use it to implement additional features like message TTL)
        var result = await channel.QueueDeclareAsync(_options.SubscriptionQueueName, _options.IsDurable, _options.IsSubscriptionQueueExclusive, _options.SubscriptionQueueAutoDelete, _options.Arguments).AnyContext();
        string queueName = result.QueueName;

        // bind the queue with the exchange.
        await channel.QueueBindAsync(queueName, _options.Topic, "").AnyContext();

        return queueName;
    }

    public override void Dispose()
    {
        base.Dispose();

        if (_isDisposed)
            return;

        _isDisposed = true;

        if (_factory != null)
            _factory.AutomaticRecoveryEnabled = false;

        ClosePublisherConnection();
        CloseSubscriberConnection();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        base.Dispose();

        if (_isDisposed)
            return;

        _isDisposed = true;

        if (_factory != null)
            _factory.AutomaticRecoveryEnabled = false;

        await ClosePublisherConnectionAsync().AnyContext();
        await CloseSubscriberConnectionAsync().AnyContext();
        GC.SuppressFinalize(this);
    }

    private void ClosePublisherConnection()
    {
        if (_publisherConnection == null)
            return;

        using (_lock.Lock())
        {
            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("ClosePublisherConnection");

            if (_publisherChannel != null)
            {
                _publisherChannel.Dispose();
                _publisherChannel = null;
            }

            if (_publisherConnection != null)
            {
                _publisherConnection.Dispose();
                _publisherConnection = null;
            }
        }
    }

    private async Task ClosePublisherConnectionAsync()
    {
        if (_publisherConnection == null)
            return;

        using (await _lock.LockAsync().AnyContext())
        {
            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("ClosePublisherConnectionAsync");

            if (_publisherChannel != null)
            {
                await _publisherChannel.DisposeAsync().AnyContext();
                _publisherChannel = null;
            }

            if (_publisherConnection != null)
            {
                await _publisherConnection.DisposeAsync().AnyContext();
                _publisherConnection = null;
            }
        }
    }

    private void CloseSubscriberConnection()
    {
        if (_subscriberConnection == null)
            return;

        using (_lock.Lock())
        {
            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("CloseSubscriberConnection");

            if (_subscriberChannel != null)
            {
                _subscriberChannel.Dispose();
                _subscriberChannel = null;
            }

            if (_subscriberConnection != null)
            {
                _subscriberConnection.Dispose();
                _subscriberConnection = null;
            }
        }
    }

    private async Task CloseSubscriberConnectionAsync()
    {
        if (_subscriberConnection == null)
            return;

        using (await _lock.LockAsync().AnyContext())
        {
            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("CloseSubscriberConnectionAsync");

            if (_subscriberChannel != null)
            {
                await _subscriberChannel.DisposeAsync().AnyContext();
                _subscriberChannel = null;
            }

            if (_subscriberConnection != null)
            {
                await _subscriberConnection.DisposeAsync().AnyContext();
                _subscriberConnection = null;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private const string XDeliveryCountHeader = "x-delivery-count";
    private const string XOriginalMessageIdHeader = "x-original-message-id";

    private readonly AsyncLock _lock = new();
    private readonly ConnectionFactory _factory;
    private readonly List<AmqpTcpEndpoint> _endpoints;
    private IConnection _publisherConnection;
    private IConnection _subscriberConnection;
    private IChannel _publisherChannel;
    private IChannel _subscriberChannel;
    private AsyncEventingBasicConsumer _consumer;
    private bool? _delayedExchangePluginEnabled;
    private readonly bool _isQuorumQueue;
    private bool _isDisposed;
    private volatile bool _isPublisherBlocked;
    private volatile string _publisherBlockedReason;

    public RabbitMQMessageBus(RabbitMQMessageBusOptions options) : base(options)
    {
        if (String.IsNullOrEmpty(options.ConnectionString))
            throw new ArgumentException("ConnectionString is required.");

        if (!Uri.TryCreate(options.ConnectionString, UriKind.Absolute, out var primaryUri))
            throw new ArgumentException($"ConnectionString is not a valid URI: {options.ConnectionString}");

        if (!primaryUri.Scheme.Equals("amqp", StringComparison.OrdinalIgnoreCase) &&
            !primaryUri.Scheme.Equals("amqps", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"ConnectionString must use amqp:// or amqps:// scheme: {options.ConnectionString}");

        _isQuorumQueue = options.Arguments is not null && options.Arguments.TryGetValue("x-queue-type", out object queueType) && queueType is string type && String.Equals(type, "quorum", StringComparison.OrdinalIgnoreCase);

        // Parse the connection string for credentials and vhost
        bool useSsl = primaryUri.Scheme.Equals("amqps", StringComparison.OrdinalIgnoreCase);
        int defaultPort = useSsl ? 5671 : 5672;

        // Build the list of endpoints for failover support
        // If Hosts is provided, use it as the complete host list; otherwise use the host from connection string
        _endpoints = [];
        var seenEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (options.Hosts is { Count: > 0 })
        {
            foreach (string host in options.Hosts)
            {
                var endpoint = ParseHostEndpoint(host, defaultPort);
                if (endpoint is not null && seenEndpoints.Add($"{endpoint.HostName}:{endpoint.Port}"))
                    _endpoints.Add(endpoint);
            }
        }
        else
        {
            _endpoints.Add(new AmqpTcpEndpoint(primaryUri.Host, primaryUri.Port > 0 ? primaryUri.Port : defaultPort));
        }

        // Initialize the connection factory with credentials/vhost from connection string
        // Automatic recovery will allow the connections to be restored in case the server is
        // restarted or there has been any network failures. TopologyRecoveryEnabled is already
        // enabled by default. NetworkRecoveryInterval is also by default set to 5 seconds.
        _factory = new ConnectionFactory
        {
            Uri = primaryUri,
            AutomaticRecoveryEnabled = true
        };
    }

    public RabbitMQMessageBus(Builder<RabbitMQMessageBusOptionsBuilder, RabbitMQMessageBusOptions> config)
        : this(config(new RabbitMQMessageBusOptionsBuilder()).Build())
    {
    }

    protected override Task RemoveTopicSubscriptionAsync()
    {
        _logger.LogTrace("RemoveTopicSubscriptionAsync");
        return CloseSubscriberConnectionAsync(DisposedCancellationToken);
    }

    protected override async Task EnsureTopicSubscriptionAsync(CancellationToken cancellationToken)
    {
        if (_subscriberChannel is not null)
            return;

        await EnsureTopicCreatedAsync(cancellationToken).AnyContext();

        using (await _lock.LockAsync(cancellationToken).AnyContext())
        {
            if (_subscriberChannel is not null)
                return;

            _subscriberConnection = await CreateConnectionAsync().AnyContext();
            RegisterSubscriberConnectionEventHandlers();

            _subscriberChannel = await _subscriberConnection.CreateChannelAsync(cancellationToken: cancellationToken).AnyContext();

            // If InitPublisher is called first, then we will never come in this if-clause.
            if (!await CreateDelayedExchangeAsync(_subscriberChannel).AnyContext())
            {
                await _subscriberChannel.DisposeAsync().AnyContext();
                UnregisterSubscriberConnectionEventHandlers();
                await _subscriberConnection.DisposeAsync().AnyContext();

                _subscriberConnection = await CreateConnectionAsync().AnyContext();
                RegisterSubscriberConnectionEventHandlers();

                _subscriberChannel = await _subscriberConnection.CreateChannelAsync(cancellationToken: cancellationToken).AnyContext();
                await CreateRegularExchangeAsync(_subscriberChannel).AnyContext();
            }

            string queueName = await CreateQueueAsync(_subscriberChannel).AnyContext();

            // Set QoS (Quality of Service) settings for the consumer
            if (_options.PrefetchCount > 0 || _options.PrefetchSize > 0)
            {
                await _subscriberChannel.BasicQosAsync(_options.PrefetchSize, _options.PrefetchCount, _options.GlobalQos, cancellationToken).AnyContext();
                _logger.LogDebug("Set channel QoS - PrefetchCount: {PrefetchCount}, PrefetchSize: {PrefetchSize}, Global: {GlobalQos} for acknowledgment strategy {AcknowledgementStrategy}",
                    _options.PrefetchCount, _options.PrefetchSize, _options.GlobalQos, _options.AcknowledgementStrategy);
            }
            else
            {
                _logger.LogDebug("Using unlimited prefetch for acknowledgment strategy {AcknowledgementStrategy}", _options.AcknowledgementStrategy);
            }

            _consumer = new AsyncEventingBasicConsumer(_subscriberChannel);
            RegisterConsumerEventHandlers();

            await _subscriberChannel.BasicConsumeAsync(queueName, _options.AcknowledgementStrategy == AcknowledgementStrategy.FireAndForget, _consumer, cancellationToken: cancellationToken).AnyContext();
            _logger.LogTrace("The unique channel number for the subscriber is : {ChannelNumber}", _subscriberChannel.ChannelNumber);
        }
    }

    private Task OnSubscriberConnectionOnCallbackExceptionAsync(object sender, CallbackExceptionEventArgs e)
    {
        _logger.LogError(e.Exception, "Subscriber callback exception: {Message}", e.Exception.Message);
        return Task.CompletedTask;
    }

    private Task OnSubscriberConnectionOnConnectionBlockedAsync(object sender, ConnectionBlockedEventArgs e)
    {
        _logger.LogError("Subscriber connection blocked: {Reason}", e.Reason);
        return Task.CompletedTask;
    }

    private Task OnSubscriberConnectionOnConnectionRecoveryErrorAsync(object sender, ConnectionRecoveryErrorEventArgs e)
    {
        _logger.LogError(e.Exception, "Subscriber connection recovery error: {Message}", e.Exception.Message);
        return Task.CompletedTask;
    }

    private Task OnSubscriberConnectionOnConnectionShutdownAsync(object sender, ShutdownEventArgs e)
    {
        _logger.LogInformation(e.Exception, "Subscriber shutdown. Reply Code: {ReplyCode} Reason: {ReplyText} Initiator: {Initiator}", e.ReplyCode, e.ReplyText, e.Initiator);
        return Task.CompletedTask;
    }

    private Task OnSubscriberConnectionOnConnectionUnblockedAsync(object sender, AsyncEventArgs e)
    {
        _logger.LogInformation("Subscriber connection unblocked");
        return Task.CompletedTask;
    }

    private Task OnSubscriberConnectionOnRecoveringConsumerAsync(object sender, RecoveringConsumerEventArgs e)
    {
        _logger.LogInformation("Subscriber connection recovering: {ConsumerTag}", e.ConsumerTag);
        return Task.CompletedTask;
    }

    private Task OnSubscriberConnectionOnRecoverySucceededAsync(object sender, AsyncEventArgs e)
    {
        _logger.LogInformation("Subscriber connection recovery succeeded");
        return Task.CompletedTask;
    }

    private Task OnConsumerShutdownAsync(object sender, ShutdownEventArgs e)
    {
        _logger.LogInformation(e.Exception, "Consumer shutdown. Reply Code: {ReplyCode} Reason: {ReplyText} Initiator: {Initiator}", e.ReplyCode, e.ReplyText, e.Initiator);
        return Task.CompletedTask;
    }

    private async Task OnMessageAsync(object sender, BasicDeliverEventArgs envelope)
    {
        using var _ = _logger.BeginScope(s => s
            .Property("MessageId", envelope.BasicProperties.MessageId)
            .Property("DeliveryTag", envelope.DeliveryTag));

        _logger.LogTrace("OnMessageAsync({MessageId})", envelope.BasicProperties.MessageId);

        if (_subscribers.IsEmpty)
        {
            _logger.LogTrace("No subscribers ({MessageId})", envelope.BasicProperties.MessageId);
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
        catch (MessageBusException)
        {
            // SendMessageToSubscribersAsync already logged the error
            if (_options.AcknowledgementStrategy == AcknowledgementStrategy.Automatic)
                await HandleDeliveryLimitsAsync(envelope).AnyContext();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling message ({MessageId}): {Message}", envelope.BasicProperties.MessageId, ex.Message);
            if (_options.AcknowledgementStrategy == AcknowledgementStrategy.Automatic)
                await HandleDeliveryLimitsAsync(envelope).AnyContext();
        }
    }

    private async Task HandleDeliveryLimitsAsync(BasicDeliverEventArgs envelope)
    {
        // Rule 1: If the limit is negative, reject regardless of queue type
        if (_options.DeliveryLimit < 0)
        {
            _logger.LogDebug("Message ({MessageId}) rejected due to negative delivery limit ({DeliveryLimit})",
                envelope.BasicProperties.MessageId, _options.DeliveryLimit);
            await _subscriberChannel.BasicRejectAsync(envelope.DeliveryTag, true).AnyContext();
            return;
        }

        // Rule 2: Determine retry count from headers
        long retryCount = GetRetryCountFromHeader(envelope);

        _logger.LogDebug("Processing message ({MessageId}) with delivery count {DeliveryCount} of {DeliveryLimit} (Queue type: {QueueType})",
            envelope.BasicProperties.MessageId, retryCount, _options.DeliveryLimit, _isQuorumQueue ? "quorum" : "classic");

        // Rule 3: Handle messages that have exceeded the delivery limit
        if (retryCount >= _options.DeliveryLimit)
        {
            if (_isQuorumQueue)
            {
                // Check if we're significantly over the limit (suggests broker config mismatch)
                if (retryCount >= _options.DeliveryLimit + 1)
                {
                    _logger.LogWarning(
                        "Quorum queue message ({MessageId}) delivery count ({DeliveryCount}) is over configured limit ({DeliveryLimit})",
                        envelope.BasicProperties.MessageId, retryCount, _options.DeliveryLimit);
                    await _subscriberChannel.BasicAckAsync(envelope.DeliveryTag, false).AnyContext();
                }
                else
                {
                    _logger.LogDebug(
                        "Quorum queue message ({MessageId}) has exceeded delivery limit ({DeliveryLimit}): Rejecting to let broker handle",
                        envelope.BasicProperties.MessageId, _options.DeliveryLimit);
                    await _subscriberChannel.BasicRejectAsync(envelope.DeliveryTag, true).AnyContext();
                }
            }
            else
            {
                _logger.LogDebug(
                    "Classic queue message ({MessageId}) has reached the delivery limit of {DeliveryLimit}: Acknowledging message",
                    envelope.BasicProperties.MessageId, _options.DeliveryLimit);
                await _subscriberChannel.BasicAckAsync(envelope.DeliveryTag, false).AnyContext();
            }

            return;
        }

        // Rule 4: Handle messages under the delivery limit
        if (_isQuorumQueue)
        {
            _logger.LogDebug(
                "Quorum queue message ({MessageId}) under delivery limit: Rejecting for broker-managed redelivery",
                envelope.BasicProperties.MessageId);
            await _subscriberChannel.BasicRejectAsync(envelope.DeliveryTag, true).AnyContext();
        }
        else
        {
            await RepublishMessageWithIncrementedDeliveryCountAsync(envelope, retryCount).AnyContext();
        }
    }

    private async Task RepublishMessageWithIncrementedDeliveryCountAsync(BasicDeliverEventArgs envelope, long currentRetryCount)
    {
        string originalMessageId = GetOriginalMessageIdFromHeader(envelope);
        var properties = new BasicProperties(envelope.BasicProperties)
        {
            MessageId = Guid.NewGuid().ToString("N")
        };

        var headers = new Dictionary<string, object>(envelope.BasicProperties.Headers ?? new Dictionary<string, object>())
        {
            [XDeliveryCountHeader] = currentRetryCount + 1,
            [XOriginalMessageIdHeader] = originalMessageId
        };

        if (!String.IsNullOrEmpty(Activity.Current?.TraceStateString))
            headers["TraceState"] = Activity.Current.TraceStateString;

        properties.Headers = headers;

        try
        {
            await EnsureTopicCreatedAsync(envelope.CancellationToken).AnyContext();
            await PublishMessageAsync(envelope.Exchange, envelope.RoutingKey, envelope.Body.ToArray(), properties, envelope.CancellationToken).AnyContext();
            await _subscriberChannel.BasicAckAsync(envelope.DeliveryTag, false).AnyContext();

            _logger.LogDebug("Republished classic queue message ({MessageId}) (OriginalMessageId={OriginalMessageId}) with delivery count {DeliveryCount}",
                envelope.BasicProperties.MessageId, originalMessageId, currentRetryCount + 1);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to republish message ({MessageId}), acknowledging to prevent infinite retry", envelope.BasicProperties.MessageId);
            await _subscriberChannel.BasicAckAsync(envelope.DeliveryTag, false).AnyContext();
        }
    }

    /// <summary>
    /// For quorum queues: x-delivery-count is set by broker (1 on first redelivery, absent on the first attempt)
    /// For classic queues: x-delivery-count is only present if we added it during previous requeue
    /// </summary>
    private static long GetRetryCountFromHeader(BasicDeliverEventArgs envelope)
    {
        long retryCount = 0;
        if (envelope.BasicProperties.Headers?.TryGetValue(XDeliveryCountHeader, out object xDeliveryCount) is true)
        {
            if (!Int64.TryParse(xDeliveryCount.ToString(), out retryCount))
                retryCount = 0;
        }

        return retryCount;
    }

    private static string GetOriginalMessageIdFromHeader(BasicDeliverEventArgs envelope)
    {
        if (envelope.BasicProperties.Headers?.TryGetValue(XOriginalMessageIdHeader, out object xOriginalMessageId) is true)
        {
            return xOriginalMessageId switch
            {
                string str => str,
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                _ => xOriginalMessageId?.ToString()
            };
        }

        return envelope.BasicProperties.MessageId;
    }

    private async Task PublishMessageAsync(string exchange, string routingKey, byte[] body, BasicProperties properties, CancellationToken cancellationToken)
    {
        using (await _lock.LockAsync(cancellationToken).AnyContext())
        {
            await _resiliencePolicy.ExecuteAsync(async _ =>
            {
                // Check blocked state inside resilience policy - re-evaluated on each retry
                // MessageBusException is excluded from retries in the base class policy
                if (_isPublisherBlocked)
                    throw new MessageBusException($"Cannot publish: publisher connection is blocked by broker ({_publisherBlockedReason ?? "resource alarm"})");

                await _publisherChannel.BasicPublishAsync(exchange, routingKey, mandatory: false, properties, body, cancellationToken: cancellationToken);
            }, cancellationToken).AnyContext();
        }
    }

    protected virtual IMessage ConvertToMessage(BasicDeliverEventArgs envelope)
    {
        var message = new Message(envelope.Body.ToArray(), DeserializeMessageBody)
        {
            Type = envelope.BasicProperties.Type,
            ClrType = GetMappedMessageType(envelope.BasicProperties.Type),
            CorrelationId = envelope.BasicProperties.CorrelationId,
            UniqueId = envelope.BasicProperties.MessageId
        };

        if (envelope.BasicProperties.Headers is not null)
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
        if (_publisherChannel is not null)
            return;

        using (await _lock.LockAsync(cancellationToken).AnyContext())
        {
            if (_publisherChannel is not null)
                return;

            // Create the client connection, channel, declares the exchange, queue and binds
            // the exchange with the publisher queue. It requires the name of our exchange, exchange type, durability and auto-delete.
            // For now, we are using same autoDelete for both exchange and queue (it will survive a server restart)
            _publisherConnection = await CreateConnectionAsync().AnyContext();
            RegisterPublisherConnectionEventHandlers();

            // Reset blocked state after handlers are registered - new connections start unblocked
            _isPublisherBlocked = false;
            _publisherBlockedReason = null;

            if (_options.PublisherConfirmsEnabled)
            {
                var channelOptions = new CreateChannelOptions(
                    publisherConfirmationsEnabled: true,
                    publisherConfirmationTrackingEnabled: true);
                _publisherChannel = await _publisherConnection.CreateChannelAsync(channelOptions, cancellationToken).AnyContext();
            }
            else
            {
                _publisherChannel = await _publisherConnection.CreateChannelAsync(cancellationToken: cancellationToken).AnyContext();
            }

            // We first attempt to create "x-delayed-type". For this plugin should be installed.
            // However, we plug in is not installed this will throw an exception. In that case
            // we attempt to create regular exchange. If regular exchange also throws and exception
            // then troubleshoot the problem.
            if (!await CreateDelayedExchangeAsync(_publisherChannel).AnyContext())
            {
                // if the initial exchange creation was not successful, then we must close the previous connection
                // and establish the new client connection and model; otherwise you will keep receiving failure in creation
                // of the regular exchange too.
                await _publisherChannel.DisposeAsync().AnyContext();
                UnregisterPublisherConnectionEventHandlers();
                await _publisherConnection.DisposeAsync().AnyContext();

                _publisherConnection = await CreateConnectionAsync().AnyContext();
                RegisterPublisherConnectionEventHandlers();

                // Reset blocked state after handlers are registered
                _isPublisherBlocked = false;
                _publisherBlockedReason = null;

                if (_options.PublisherConfirmsEnabled)
                {
                    var channelOptions = new CreateChannelOptions(
                        publisherConfirmationsEnabled: true,
                        publisherConfirmationTrackingEnabled: true);
                    _publisherChannel = await _publisherConnection.CreateChannelAsync(channelOptions, cancellationToken).AnyContext();
                }
                else
                {
                    _publisherChannel = await _publisherConnection.CreateChannelAsync(cancellationToken: cancellationToken).AnyContext();
                }
                await CreateRegularExchangeAsync(_publisherChannel).AnyContext();
            }

            _logger.LogTrace("The unique channel number for the publisher is : {ChannelNumber}", _publisherChannel.ChannelNumber);
        }
    }

    private Task OnPublisherConnectionOnCallbackExceptionAsync(object sender, CallbackExceptionEventArgs e)
    {
        _logger.LogError(e.Exception, "Publisher callback exception: {Message}", e.Exception.Message);
        return Task.CompletedTask;
    }

    private Task OnPublisherConnectionOnConnectionBlockedAsync(object sender, ConnectionBlockedEventArgs e)
    {
        _publisherBlockedReason = e.Reason;
        _isPublisherBlocked = true;
        _logger.LogError("Publisher connection blocked: {Reason}", e.Reason);
        return Task.CompletedTask;
    }

    private Task OnPublisherConnectionOnConnectionRecoveryErrorAsync(object sender, ConnectionRecoveryErrorEventArgs e)
    {
        _logger.LogError(e.Exception, "Publisher connection recovery error: {Message}", e.Exception.Message);
        return Task.CompletedTask;
    }

    private Task OnPublisherConnectionOnConnectionShutdownAsync(object sender, ShutdownEventArgs e)
    {
        _logger.LogInformation(e.Exception, "Publisher shutdown. Reply Code: {ReplyCode} Reason: {ReplyText} Initiator: {Initiator}", e.ReplyCode, e.ReplyText, e.Initiator);
        return Task.CompletedTask;
    }

    private Task OnPublisherConnectionOnConnectionUnblockedAsync(object sender, AsyncEventArgs e)
    {
        _isPublisherBlocked = false;
        _publisherBlockedReason = null;
        _logger.LogInformation("Publisher connection unblocked");
        return Task.CompletedTask;
    }

    private Task OnPublisherConnectionOnRecoveringConsumerAsync(object sender, RecoveringConsumerEventArgs e)
    {
        _logger.LogInformation("Publisher connection recovering: {ConsumerTag}", e.ConsumerTag);
        return Task.CompletedTask;
    }

    private Task OnPublisherConnectionOnRecoverySucceededAsync(object sender, AsyncEventArgs e)
    {
        // Reset blocked state on recovery - the new connection starts unblocked
        _isPublisherBlocked = false;
        _publisherBlockedReason = null;
        _logger.LogInformation("Publisher connection recovery succeeded");
        return Task.CompletedTask;
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
        if (_isPublisherBlocked)
            throw new MessageBusException($"Cannot publish: publisher connection is blocked by broker ({_publisherBlockedReason ?? "resource alarm"})");

        byte[] data = SerializeMessageBody(messageType, message);

        // if the RabbitMQ plugin is not available, then use the base class delay mechanism
        if (_delayedExchangePluginEnabled is false && options.DeliveryDelay.HasValue && options.DeliveryDelay.Value > TimeSpan.Zero)
        {
            var mappedType = GetMappedMessageType(messageType);
            _logger.LogTrace("Schedule delayed message: {MessageType} ({Delay}ms)", messageType, options.DeliveryDelay.Value.TotalMilliseconds);

            SendDelayedMessage(mappedType, message, options);
            return;
        }

        var basicProperties = new BasicProperties
        {
            MessageId = options.UniqueId ?? Guid.NewGuid().ToString("N"),
            CorrelationId = options.CorrelationId,
            Type = messageType
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
        if (_delayedExchangePluginEnabled is true && options.DeliveryDelay.HasValue && options.DeliveryDelay.Value > TimeSpan.Zero)
        {
            // It's necessary to typecast long to int because RabbitMQ on the consumer side is reading the
            // data back as signed (using BinaryReader#ReadInt64). You will see the value to be negative
            // and the data will be delivered immediately.
            basicProperties.Headers ??= new Dictionary<string, object>();
            basicProperties.Headers["x-delay"] = Convert.ToInt32(options.DeliveryDelay.Value.TotalMilliseconds);
            _logger.LogTrace("Schedule delayed message: {MessageType} ({Delay}ms)", messageType, options.DeliveryDelay.Value.TotalMilliseconds);
        }
        else
        {
            _logger.LogTrace("Message publish type {MessageType} {MessageId}", messageType, basicProperties.MessageId);
        }

        await PublishMessageAsync(_options.Topic, String.Empty, data, basicProperties, cancellationToken).AnyContext();
        _logger.LogDebug("Done publishing type {MessageType} {MessageId}", messageType, basicProperties.MessageId);
    }

    /// <summary>
    /// Connect to a broker - RabbitMQ
    /// </summary>
    /// <returns></returns>
    private Task<IConnection> CreateConnectionAsync()
    {
        // Use multiple endpoints for failover support
        return _factory.CreateConnectionAsync(_endpoints);
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
            // Disclaimer: https://github.com/rabbitmq/rabbitmq-delayed-message-exchange/
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

        if (_factory is not null)
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

        if (_factory is not null)
            _factory.AutomaticRecoveryEnabled = false;

        await ClosePublisherConnectionAsync().AnyContext();
        await CloseSubscriberConnectionAsync().AnyContext();
        GC.SuppressFinalize(this);
    }

    private void ClosePublisherConnection(CancellationToken cancellationToken = default)
    {
        if (_publisherConnection is null)
            return;

        using (_lock.Lock(cancellationToken))
        {
            _logger.LogTrace("ClosePublisherConnection");

            if (_publisherChannel is not null)
            {
                _publisherChannel.Dispose();
                _publisherChannel = null;
            }

            if (_publisherConnection is not null)
            {
                UnregisterPublisherConnectionEventHandlers();
                _publisherConnection.Dispose();
                _publisherConnection = null;
            }
        }
    }

    private async Task ClosePublisherConnectionAsync()
    {
        if (_publisherConnection is null)
            return;

        using (await _lock.LockAsync().AnyContext())
        {
            _logger.LogTrace("ClosePublisherConnectionAsync");

            if (_publisherChannel is not null)
            {
                await _publisherChannel.DisposeAsync().AnyContext();
                _publisherChannel = null;
            }

            if (_publisherConnection is not null)
            {
                UnregisterPublisherConnectionEventHandlers();
                await _publisherConnection.DisposeAsync().AnyContext();
                _publisherConnection = null;
            }
        }
    }

    private void CloseSubscriberConnection(CancellationToken cancellationToken = default)
    {
        if (_subscriberConnection is null)
            return;

        using (_lock.Lock(cancellationToken))
        {
            _logger.LogTrace("CloseSubscriberConnection");

            if (_consumer is not null)
            {
                UnregisterConsumerEventHandlers();
                _consumer = null;
            }

            if (_subscriberChannel is not null)
            {
                _subscriberChannel.Dispose();
                _subscriberChannel = null;
            }

            if (_subscriberConnection is not null)
            {
                UnregisterSubscriberConnectionEventHandlers();
                _subscriberConnection.Dispose();
                _subscriberConnection = null;
            }
        }
    }

    private async Task CloseSubscriberConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (_subscriberConnection is null)
            return;

        using (await _lock.LockAsync(cancellationToken).AnyContext())
        {
            _logger.LogTrace("CloseSubscriberConnectionAsync");

            if (_consumer is not null)
            {
                UnregisterConsumerEventHandlers();
                _consumer = null;
            }

            if (_subscriberChannel is not null)
            {
                await _subscriberChannel.DisposeAsync().AnyContext();
                _subscriberChannel = null;
            }

            if (_subscriberConnection is not null)
            {
                UnregisterSubscriberConnectionEventHandlers();
                await _subscriberConnection.DisposeAsync().AnyContext();
                _subscriberConnection = null;
            }
        }
    }

    /// <summary>
    /// Parses a host string in format "hostname" or "hostname:port" into an AmqpTcpEndpoint.
    /// </summary>
    private static AmqpTcpEndpoint ParseHostEndpoint(string host, int defaultPort)
    {
        if (String.IsNullOrWhiteSpace(host))
            return null;

        string trimmed = host.Trim();
        int colonIndex = trimmed.LastIndexOf(':');
        if (colonIndex < 0)
            return new AmqpTcpEndpoint(trimmed, defaultPort);

        string hostname = trimmed[..colonIndex];
        return Int32.TryParse(trimmed[(colonIndex + 1)..], out int port)
            ? new AmqpTcpEndpoint(hostname, port)
            : new AmqpTcpEndpoint(trimmed, defaultPort);
    }

    private void RegisterPublisherConnectionEventHandlers()
    {
        _publisherConnection.CallbackExceptionAsync += OnPublisherConnectionOnCallbackExceptionAsync;
        _publisherConnection.ConnectionBlockedAsync += OnPublisherConnectionOnConnectionBlockedAsync;
        _publisherConnection.ConnectionRecoveryErrorAsync += OnPublisherConnectionOnConnectionRecoveryErrorAsync;
        _publisherConnection.ConnectionShutdownAsync += OnPublisherConnectionOnConnectionShutdownAsync;
        _publisherConnection.ConnectionUnblockedAsync += OnPublisherConnectionOnConnectionUnblockedAsync;
        _publisherConnection.RecoveringConsumerAsync += OnPublisherConnectionOnRecoveringConsumerAsync;
        _publisherConnection.RecoverySucceededAsync += OnPublisherConnectionOnRecoverySucceededAsync;
    }

    private void UnregisterPublisherConnectionEventHandlers()
    {
        _publisherConnection.CallbackExceptionAsync -= OnPublisherConnectionOnCallbackExceptionAsync;
        _publisherConnection.ConnectionBlockedAsync -= OnPublisherConnectionOnConnectionBlockedAsync;
        _publisherConnection.ConnectionRecoveryErrorAsync -= OnPublisherConnectionOnConnectionRecoveryErrorAsync;
        _publisherConnection.ConnectionShutdownAsync -= OnPublisherConnectionOnConnectionShutdownAsync;
        _publisherConnection.ConnectionUnblockedAsync -= OnPublisherConnectionOnConnectionUnblockedAsync;
        _publisherConnection.RecoveringConsumerAsync -= OnPublisherConnectionOnRecoveringConsumerAsync;
        _publisherConnection.RecoverySucceededAsync -= OnPublisherConnectionOnRecoverySucceededAsync;
    }

    private void RegisterSubscriberConnectionEventHandlers()
    {
        _subscriberConnection.CallbackExceptionAsync += OnSubscriberConnectionOnCallbackExceptionAsync;
        _subscriberConnection.ConnectionBlockedAsync += OnSubscriberConnectionOnConnectionBlockedAsync;
        _subscriberConnection.ConnectionRecoveryErrorAsync += OnSubscriberConnectionOnConnectionRecoveryErrorAsync;
        _subscriberConnection.ConnectionShutdownAsync += OnSubscriberConnectionOnConnectionShutdownAsync;
        _subscriberConnection.ConnectionUnblockedAsync += OnSubscriberConnectionOnConnectionUnblockedAsync;
        _subscriberConnection.RecoveringConsumerAsync += OnSubscriberConnectionOnRecoveringConsumerAsync;
        _subscriberConnection.RecoverySucceededAsync += OnSubscriberConnectionOnRecoverySucceededAsync;
    }

    private void UnregisterSubscriberConnectionEventHandlers()
    {
        _subscriberConnection.CallbackExceptionAsync -= OnSubscriberConnectionOnCallbackExceptionAsync;
        _subscriberConnection.ConnectionBlockedAsync -= OnSubscriberConnectionOnConnectionBlockedAsync;
        _subscriberConnection.ConnectionRecoveryErrorAsync -= OnSubscriberConnectionOnConnectionRecoveryErrorAsync;
        _subscriberConnection.ConnectionShutdownAsync -= OnSubscriberConnectionOnConnectionShutdownAsync;
        _subscriberConnection.ConnectionUnblockedAsync -= OnSubscriberConnectionOnConnectionUnblockedAsync;
        _subscriberConnection.RecoveringConsumerAsync -= OnSubscriberConnectionOnRecoveringConsumerAsync;
        _subscriberConnection.RecoverySucceededAsync -= OnSubscriberConnectionOnRecoverySucceededAsync;
    }

    private void RegisterConsumerEventHandlers()
    {
        _consumer.ReceivedAsync += OnMessageAsync;
        _consumer.ShutdownAsync += OnConsumerShutdownAsync;
    }

    private void UnregisterConsumerEventHandlers()
    {
        _consumer.ReceivedAsync -= OnMessageAsync;
        _consumer.ShutdownAsync -= OnConsumerShutdownAsync;
    }
}

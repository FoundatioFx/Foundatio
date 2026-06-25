using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
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

public class RabbitMQMessageBus : MessageBusBase<RabbitMQMessageBusOptions>
{
    private const string XDeliveryCountHeader = "x-delivery-count";
    private const string XOriginalMessageIdHeader = "x-original-message-id";
    private const string PriorityPropertyKey = "Priority";
    private static readonly Version _delayedExchangePluginIncompatibleVersion = new(4, 3);
    private static readonly Version _globalQosRemovedVersion = new(4, 3);

    private readonly AsyncLock _lock = new();
    private readonly AsyncManualResetEvent _publisherReady = new(true);
    private readonly ConnectionFactory _factory;
    private readonly List<AmqpTcpEndpoint> _endpoints;
    private IConnection? _publisherConnection;
    private IConnection? _subscriberConnection;
    private volatile IChannel? _publisherChannel;
    private volatile IChannel? _subscriberChannel;
    private AsyncEventingBasicConsumer? _consumer;
    private bool? _delayedExchangePluginEnabled;
    private Version? _serverVersion;
    private readonly bool _isQuorumQueue;
    private volatile bool _isPublisherBlocked;
    private volatile string? _publisherBlockedReason;

    public RabbitMQMessageBus(RabbitMQMessageBusOptions options) : base(options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options?.ConnectionString, nameof(options.ConnectionString));

        if (!Uri.TryCreate(options.ConnectionString, UriKind.Absolute, out var primaryUri))
            throw new ArgumentException("ConnectionString is not a valid URI.");

        if (!primaryUri.Scheme.Equals("amqp", StringComparison.OrdinalIgnoreCase) &&
            !primaryUri.Scheme.Equals("amqps", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"ConnectionString must use amqp:// or amqps:// scheme: {SanitizeUri(primaryUri)}");

        _isQuorumQueue = options.Arguments is not null && options.Arguments.TryGetValue("x-queue-type", out object? queueType) && queueType is string type && String.Equals(type, "quorum", StringComparison.OrdinalIgnoreCase);

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

        if (options.RequestedHeartbeat.HasValue)
            _factory.RequestedHeartbeat = options.RequestedHeartbeat.Value;

        if (options.NetworkRecoveryInterval.HasValue)
            _factory.NetworkRecoveryInterval = options.NetworkRecoveryInterval.Value;
    }

    public RabbitMQMessageBus(Builder<RabbitMQMessageBusOptionsBuilder, RabbitMQMessageBusOptions> config)
        : this(config(new RabbitMQMessageBusOptionsBuilder()).Build())
    {
    }

    protected override async Task RemoveTopicSubscriptionAsync()
    {
        await CloseSubscriberConnectionAsync().AnyContext();
    }

    protected override async Task CleanupAsync()
    {
        _factory.AutomaticRecoveryEnabled = false;

        await ClosePublisherConnectionAsync().AnyContext();
        await CloseSubscriberConnectionAsync().AnyContext();

        _publisherReady.Set();
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
            DetectServerVersion(_subscriberConnection);
            RegisterSubscriberConnectionEventHandlers();

            _subscriberChannel = await _subscriberConnection.CreateChannelAsync(cancellationToken: cancellationToken).AnyContext();

            // If InitPublisher is called first, then we will never come in this if-clause.
            var delayedExchangeResult = await CreateDelayedExchangeAsync(_subscriberChannel).AnyContext();
            if (delayedExchangeResult is null)
            {
                await CreateRegularExchangeAsync(_subscriberChannel).AnyContext();
            }
            else if (delayedExchangeResult is false)
            {
                await _subscriberChannel.DisposeAsync().AnyContext();
                UnregisterSubscriberConnectionEventHandlers();
                await _subscriberConnection.DisposeAsync().AnyContext();

                _subscriberConnection = await CreateConnectionAsync().AnyContext();
                DetectServerVersion(_subscriberConnection);
                RegisterSubscriberConnectionEventHandlers();

                _subscriberChannel = await _subscriberConnection.CreateChannelAsync(cancellationToken: cancellationToken).AnyContext();
                await CreateRegularExchangeAsync(_subscriberChannel).AnyContext();
            }

            string queueName = await CreateQueueAsync(_subscriberChannel).AnyContext();

            // Set QoS (Quality of Service) settings for the consumer
            if (_options.PrefetchCount > 0 || _options.PrefetchSize > 0)
            {
#pragma warning disable CS0618 // GlobalQos is obsolete but we still need to read it for backward compatibility
                bool useGlobalQos = _options.GlobalQos;
#pragma warning restore CS0618
                if (useGlobalQos && _isQuorumQueue)
                {
                    _logger.LogWarning("GlobalQos is not supported on quorum queues. Falling back to per-channel prefetch (global: false). Remove the GlobalQos option to suppress this warning");
                    useGlobalQos = false;
                }
                else if (useGlobalQos && _serverVersion is not null && _serverVersion >= _globalQosRemovedVersion)
                {
                    _logger.LogWarning("GlobalQos is not supported on RabbitMQ {ServerVersion}. Falling back to per-channel prefetch (global: false). Remove the GlobalQos option to suppress this warning", _serverVersion);
                    useGlobalQos = false;
                }
                else if (useGlobalQos && _serverVersion is not null)
                {
                    _logger.LogWarning("GlobalQos is deprecated in RabbitMQ 4.3+ and will be removed in a future version. Use per-channel prefetch instead");
                }

                await _subscriberChannel.BasicQosAsync(_options.PrefetchSize, _options.PrefetchCount, useGlobalQos, cancellationToken).AnyContext();
                _logger.LogDebug("Set channel QoS - PrefetchCount: {PrefetchCount}, PrefetchSize: {PrefetchSize}, Global: {GlobalQos} for acknowledgment strategy {AcknowledgementStrategy}",
                    _options.PrefetchCount, _options.PrefetchSize, useGlobalQos, _options.AcknowledgementStrategy);
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
        if (_subscriberChannel is not { } subscriberChannel)
        {
            _logger.LogDebug("Ignoring message because subscriber channel is not available ({MessageId})",
                envelope.BasicProperties?.MessageId);
            return;
        }

        using var _ = _logger.BeginScope(s => s
            .Property("MessageId", envelope.BasicProperties.MessageId)
            .Property("DeliveryTag", envelope.DeliveryTag));

        _logger.LogTrace("OnMessageAsync({MessageId})", envelope.BasicProperties.MessageId);

        if (_subscribers.IsEmpty)
        {
            _logger.LogTrace("No subscribers ({MessageId})", envelope.BasicProperties.MessageId);
            if (_options.AcknowledgementStrategy == AcknowledgementStrategy.Automatic)
                await subscriberChannel.BasicRejectAsync(envelope.DeliveryTag, true).AnyContext();

            return;
        }

        try
        {
            var message = ConvertToMessage(envelope);
            await SendMessageToSubscribersAsync(message).AnyContext();

            if (_options.AcknowledgementStrategy == AcknowledgementStrategy.Automatic)
                await subscriberChannel.BasicAckAsync(envelope.DeliveryTag, false).AnyContext();
        }
        catch (OperationCanceledException)
        {
            if (_options.AcknowledgementStrategy == AcknowledgementStrategy.Automatic)
                await subscriberChannel.BasicRejectAsync(envelope.DeliveryTag, true).AnyContext();
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
        if (_subscriberChannel is not { } subscriberChannel)
        {
            _logger.LogWarning("Subscriber channel is not available; skipping delivery limit handling for message ({MessageId})", envelope.BasicProperties.MessageId);
            return;
        }

        // Rule 1: If the limit is negative, reject regardless of queue type
        if (_options.DeliveryLimit < 0)
        {
            _logger.LogDebug("Message ({MessageId}) rejected due to negative delivery limit ({DeliveryLimit})",
                envelope.BasicProperties.MessageId, _options.DeliveryLimit);
            await subscriberChannel.BasicRejectAsync(envelope.DeliveryTag, true).AnyContext();
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
                    await subscriberChannel.BasicAckAsync(envelope.DeliveryTag, false).AnyContext();
                }
                else
                {
                    _logger.LogDebug(
                        "Quorum queue message ({MessageId}) has exceeded delivery limit ({DeliveryLimit}): Rejecting to let broker handle",
                        envelope.BasicProperties.MessageId, _options.DeliveryLimit);
                    await subscriberChannel.BasicRejectAsync(envelope.DeliveryTag, true).AnyContext();
                }
            }
            else
            {
                _logger.LogDebug(
                    "Classic queue message ({MessageId}) has reached the delivery limit of {DeliveryLimit}: Acknowledging message",
                    envelope.BasicProperties.MessageId, _options.DeliveryLimit);
                await subscriberChannel.BasicAckAsync(envelope.DeliveryTag, false).AnyContext();
            }

            return;
        }

        // Rule 4: Handle messages under the delivery limit
        if (_isQuorumQueue)
        {
            _logger.LogDebug(
                "Quorum queue message ({MessageId}) under delivery limit: Rejecting for broker-managed redelivery",
                envelope.BasicProperties.MessageId);
            await subscriberChannel.BasicRejectAsync(envelope.DeliveryTag, true).AnyContext();
        }
        else
        {
            await RepublishMessageWithIncrementedDeliveryCountAsync(envelope, retryCount).AnyContext();
        }
    }

    private async Task RepublishMessageWithIncrementedDeliveryCountAsync(BasicDeliverEventArgs envelope, long currentRetryCount)
    {
        if (_subscriberChannel is not { } subscriberChannel)
        {
            _logger.LogWarning("Skipping republish for message ({MessageId}) because the subscriber channel is unavailable; leaving message unacknowledged for broker redelivery",
                envelope.BasicProperties.MessageId);
            return;
        }

        string? originalMessageId = GetOriginalMessageIdFromHeader(envelope);
        var properties = new BasicProperties(envelope.BasicProperties)
        {
            MessageId = Guid.NewGuid().ToString("N")
        };

        var headers = new Dictionary<string, object?>(envelope.BasicProperties.Headers ?? new Dictionary<string, object?>())
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
            await PublishMessageAsync(envelope.Exchange, envelope.RoutingKey, envelope.Body, properties, envelope.CancellationToken).AnyContext();
            await subscriberChannel.BasicAckAsync(envelope.DeliveryTag, false).AnyContext();

            _logger.LogDebug("Republished classic queue message ({MessageId}) (OriginalMessageId={OriginalMessageId}) with delivery count {DeliveryCount}",
                envelope.BasicProperties.MessageId, originalMessageId, currentRetryCount + 1);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to republish message ({MessageId}), acknowledging to prevent infinite retry", envelope.BasicProperties.MessageId);
            await subscriberChannel.BasicAckAsync(envelope.DeliveryTag, false).AnyContext();
        }
    }

    /// <summary>
    /// For quorum queues: x-delivery-count is set by broker (1 on first redelivery, absent on the first attempt)
    /// For classic queues: x-delivery-count is only present if we added it during previous requeue
    /// </summary>
    private static long GetRetryCountFromHeader(BasicDeliverEventArgs envelope)
    {
        long retryCount = 0;
        if (envelope.BasicProperties.Headers?.TryGetValue(XDeliveryCountHeader, out object? xDeliveryCount) is true)
        {
            if (!Int64.TryParse(xDeliveryCount?.ToString(), out retryCount))
                retryCount = 0;
        }

        return retryCount;
    }

    private static string? GetOriginalMessageIdFromHeader(BasicDeliverEventArgs envelope)
    {
        if (envelope.BasicProperties.Headers?.TryGetValue(XOriginalMessageIdHeader, out object? xOriginalMessageId) is true)
        {
            return xOriginalMessageId switch
            {
                string str => str,
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                null => envelope.BasicProperties.MessageId,
                _ => xOriginalMessageId.ToString() ?? envelope.BasicProperties.MessageId
            };
        }

        return envelope.BasicProperties.MessageId;
    }

    private async Task PublishMessageAsync(string exchange, string routingKey, ReadOnlyMemory<byte> body, BasicProperties properties, CancellationToken cancellationToken)
    {
        await _resiliencePolicy.ExecuteAsync(async _ =>
        {
            if (!_publisherReady.IsSet)
            {
                if (_options.PublishRecoveryTimeout <= TimeSpan.Zero)
                    throw new MessageBusException("Cannot publish: publisher channel is closed or unavailable.");

                _logger.LogDebug("Publisher waiting for connection recovery...");
                try
                {
                    await _publisherReady.WaitAsync(cancellationToken)
                        .WaitAsync(_options.PublishRecoveryTimeout, cancellationToken).AnyContext();
                }
                catch (TimeoutException)
                {
                    throw new MessageBusException(
                        $"Publish failed: connection recovery did not complete within {_options.PublishRecoveryTimeout.TotalMilliseconds:F0}ms timeout.");
                }
            }

            // PERF: Single lock serializes all publishes; with publisher confirms this limits throughput to 1 RTT.
            // Consider channel pooling or batch publishing for high-throughput scenarios.
            using (await _lock.LockAsync(cancellationToken).AnyContext())
            {
                if (_publisherChannel is not { IsOpen: true } channel)
                    throw new MessageBusException("Cannot publish: publisher channel is closed or unavailable.");

                // Fail fast on broker resource alarms -- retrying would add pressure to a constrained broker.
                // Unlike connection drops (which use the recovery gate to wait), blocked state has no recovery signal timing.
                if (_isPublisherBlocked)
                    throw new MessageBusException(
                        $"Cannot publish: publisher connection is blocked by broker ({_publisherBlockedReason ?? "resource alarm"})");

                await channel.BasicPublishAsync(exchange, routingKey, mandatory: false, properties, body, cancellationToken: cancellationToken);
            }
        }, cancellationToken).AnyContext();
    }

    protected virtual IMessage ConvertToMessage(BasicDeliverEventArgs envelope)
    {
        // Zero-copy: envelope.Body is a pooled buffer that RabbitMQ.Client reclaims once OnMessageAsync
        // returns. Referencing it without copying is safe because the body is deserialized synchronously
        // within the awaited SendMessageToSubscribersAsync dispatch, before the handler returns. Per the
        // IMessage.Data contract, the buffer is only valid for the duration of handling; consumers that
        // retain the raw payload beyond the handler must copy it via ToArray().
        var message = new Message(envelope.Body, DeserializeMessageBody)
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
                else if (header.Value?.ToString() is { } stringValue)
                    message.Properties[header.Key] = stringValue;
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
            DetectServerVersion(_publisherConnection);
            RegisterPublisherConnectionEventHandlers();

            // Reset blocked state after handlers are registered - new connections start unblocked
            _isPublisherBlocked = false;
            _publisherBlockedReason = null;

            _publisherChannel = await CreatePublisherChannelAsync(cancellationToken).AnyContext();

            // We first attempt to create "x-delayed-type". For this the rabbitmq_delayed_message_exchange plugin should be installed.
            // However, if the plugin is not installed this will throw an exception. In that case
            // we attempt to create a regular exchange. If the regular exchange also throws an exception
            // then troubleshoot the problem.
            var delayedExchangeResult = await CreateDelayedExchangeAsync(_publisherChannel).AnyContext();
            if (delayedExchangeResult is null)
            {
                await CreateRegularExchangeAsync(_publisherChannel).AnyContext();
            }
            else if (delayedExchangeResult is false)
            {
                // if the initial exchange creation was not successful, then we must close the previous connection
                // and establish the new client connection and model; otherwise you will keep receiving failure in creation
                // of the regular exchange too.
                await _publisherChannel.DisposeAsync().AnyContext();
                UnregisterPublisherConnectionEventHandlers();
                await _publisherConnection.DisposeAsync().AnyContext();

                _publisherConnection = await CreateConnectionAsync().AnyContext();
                DetectServerVersion(_publisherConnection);
                RegisterPublisherConnectionEventHandlers();

                // Reset blocked state after handlers are registered
                _isPublisherBlocked = false;
                _publisherBlockedReason = null;

                _publisherChannel = await CreatePublisherChannelAsync(cancellationToken).AnyContext();
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
        _logger.LogError(e.Exception, "Publisher connection recovery attempt failed, retrying: {Message}", e.Exception.Message);
        return Task.CompletedTask;
    }

    private Task OnPublisherConnectionOnConnectionShutdownAsync(object sender, ShutdownEventArgs e)
    {
        if (e.Initiator != ShutdownInitiator.Application)
        {
            _publisherReady.Reset();
            if (_options.PublishRecoveryTimeout > TimeSpan.Zero)
                _logger.LogWarning("Publisher connection lost (Reply Code: {ReplyCode}, Reason: {ReplyText}). Publishes will wait up to {Timeout:g} for recovery.",
                    e.ReplyCode, e.ReplyText, _options.PublishRecoveryTimeout);
            else
                _logger.LogWarning("Publisher connection lost (Reply Code: {ReplyCode}, Reason: {ReplyText}). Publishes will fail immediately (recovery timeout disabled).",
                    e.ReplyCode, e.ReplyText);
        }

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
        _isPublisherBlocked = false;
        _publisherBlockedReason = null;
        _publisherReady.Set();
        _logger.LogInformation("Publisher connection recovery succeeded");
        return Task.CompletedTask;
    }

    protected override async Task PublishImplAsync(string messageType, object message, MessageOptions options, CancellationToken cancellationToken)
    {
        byte[] data = SerializeMessageBody(messageType, message);

        // if the RabbitMQ plugin is not available, then use the base class delay mechanism
        if (_delayedExchangePluginEnabled is false && options.DeliveryDelay.HasValue && options.DeliveryDelay.Value > TimeSpan.Zero)
        {
            _logger.LogTrace("Delayed message will be scheduled in-memory (broker-side delayed exchange unavailable): {MessageType} ({Delay}ms)", messageType, options.DeliveryDelay.Value.TotalMilliseconds);
            var mappedType = GetMappedMessageType(messageType);
            if (mappedType is null)
                throw new MessageBusException($"Unable to resolve CLR type for delayed message: {messageType}");

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

        if (options.Properties.TryGetValue(PriorityPropertyKey, out string? priorityValue) && Byte.TryParse(priorityValue, out byte priority))
            basicProperties.Priority = priority;

        if (options.Properties.Count > 0)
        {
            basicProperties.Headers ??= new Dictionary<string, object?>();
            foreach (var property in options.Properties.Where(p => !String.Equals(p.Key, PriorityPropertyKey, StringComparison.Ordinal)))
            {
                basicProperties.Headers.Add(property.Key, property.Value);
            }
        }

        // RabbitMQ only supports delayed messages with a third party plugin called "rabbitmq_delayed_message_exchange"
        if (_delayedExchangePluginEnabled is true && options.DeliveryDelay.HasValue && options.DeliveryDelay.Value > TimeSpan.Zero)
        {
            // RabbitMQ's x-delay header must be a 32-bit signed int; the broker reads it as Int32
            // and negative values cause immediate delivery.
            basicProperties.Headers ??= new Dictionary<string, object?>();
            double delayMs = options.DeliveryDelay.Value.TotalMilliseconds;
            if (delayMs > Int32.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(options), $"DeliveryDelay ({options.DeliveryDelay.Value}) exceeds the maximum supported by RabbitMQ delayed exchange plugin ({Int32.MaxValue}ms).");
            basicProperties.Headers["x-delay"] = (int)delayMs;
            _logger.LogTrace("Schedule delayed message: {MessageType} ({Delay}ms)", messageType, options.DeliveryDelay.Value.TotalMilliseconds);
        }
        else
        {
            _logger.LogTrace("Message publish type {MessageType} {MessageId}", messageType, basicProperties.MessageId);
        }

        await PublishMessageAsync(_options.Topic, String.Empty, data, basicProperties, cancellationToken).AnyContext();
        _logger.LogDebug("Done publishing type {MessageType} {MessageId}", messageType, basicProperties.MessageId);
    }

    private Task<IConnection> CreateConnectionAsync()
    {
        return _factory.CreateConnectionAsync(_endpoints);
    }

    private Task<IChannel> CreatePublisherChannelAsync(CancellationToken cancellationToken)
    {
        if (_publisherConnection is null)
            throw new MessageBusException("Publisher connection must be initialized before creating a channel.");

        if (_options.PublisherConfirmsEnabled)
        {
            var channelOptions = new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true);
            return _publisherConnection.CreateChannelAsync(channelOptions, cancellationToken);
        }

        return _publisherConnection.CreateChannelAsync(cancellationToken: cancellationToken);
    }

    private void DetectServerVersion(IConnection connection)
    {
        if (_serverVersion is not null)
            return;

        var version = ParseServerVersion(connection.ServerProperties);
        if (version is null)
            return;

        _serverVersion = version;
        _logger.LogDebug("Connected to RabbitMQ server version {ServerVersion}", version);
    }

    /// <summary>
    /// Parses the RabbitMQ broker version from AMQP server properties (the <c>version</c> field is UTF-8 bytes).
    /// </summary>
    public static Version? ParseServerVersion(IDictionary<string, object?>? serverProperties)
    {
        if (serverProperties?.TryGetValue("version", out var versionObj) is not true
            || versionObj is not byte[] bytes)
            return null;

        return Version.TryParse(Encoding.UTF8.GetString(bytes), out var version) ? version : null;
    }

    /// <summary>
    /// Attempts to create the delayed exchange. On RabbitMQ 4.3+ the probe is skipped because the
    /// rabbitmq_delayed_message_exchange plugin depends on Mnesia which was removed. When the server
    /// version could not be determined, the probe is still attempted so the plugin is used if available.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the delayed exchange was successfully declared (plugin is installed);
    /// <c>null</c> if the probe was skipped or the result was already cached as disabled (the channel is still healthy);
    /// <c>false</c> if the probe threw and the channel is likely closed.
    /// </returns>
    private async Task<bool?> CreateDelayedExchangeAsync(IChannel channel)
    {
        if (_delayedExchangePluginEnabled.HasValue)
            return _delayedExchangePluginEnabled.Value ? true : null;

        if (_serverVersion is not null && _serverVersion >= _delayedExchangePluginIncompatibleVersion)
        {
            _logger.LogWarning(
                "The rabbitmq_delayed_message_exchange plugin is incompatible with RabbitMQ {ServerVersion} (Mnesia removed). Delayed messages will be scheduled in-memory. Support for this plugin will be removed in a future version",
                _serverVersion);
            _delayedExchangePluginEnabled = false;
            return null;
        }

        bool success = true;
        try
        {
            // This exchange is a delayed exchange (fanout). You need the rabbitmq_delayed_message_exchange plugin on RabbitMQ.
            // Disclaimer: https://github.com/rabbitmq/rabbitmq-delayed-message-exchange/
            // Please read the *Performance Impact* of the delayed exchange type.
            // On RabbitMQ 4.3+ this probe is skipped (see method summary); the plugin is incompatible.
            var args = new Dictionary<string, object?> { { "x-delayed-type", ExchangeType.Fanout } };
            await channel.ExchangeDeclareAsync(_options.Topic, "x-delayed-message", _options.IsDurable, false, args).AnyContext();
        }
        catch (OperationInterruptedException ex)
        {
            _logger.LogInformation(
                ex,
                "Unable to declare delayed exchange. ReplyCode: {ReplyCode}, ReplyText: {ReplyText}, Message: {Message}",
                ex.ShutdownReason?.ReplyCode,
                ex.ShutdownReason?.ReplyText,
                ex.Message);
            success = false;
        }

        if (success)
        {
            _logger.LogWarning(
                "The rabbitmq_delayed_message_exchange plugin is deprecated and incompatible with RabbitMQ 4.3+. Support will be removed in a future version and delayed messages will fall back to in-memory scheduling. See https://github.com/rabbitmq/rabbitmq-delayed-message-exchange for details");
        }

        _delayedExchangePluginEnabled = success;
        return success;
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
        var arguments = _options.Arguments is not null
            ? new Dictionary<string, object?>(_options.Arguments)
            : new Dictionary<string, object?>();

        if (!String.IsNullOrWhiteSpace(_options.DeadLetterExchange))
        {
            arguments["x-dead-letter-exchange"] = _options.DeadLetterExchange;

            if (!String.IsNullOrWhiteSpace(_options.DeadLetterRoutingKey))
                arguments["x-dead-letter-routing-key"] = _options.DeadLetterRoutingKey;

            if (_options.DeadLetterStrategy.HasValue)
            {
                if (_options.DeadLetterStrategy == DeadLetterStrategy.AtLeastOnce)
                {
                    if (!_isQuorumQueue)
                        throw new MessageBusException("At-least-once dead-lettering requires quorum queues. Call UseQuorumQueues().");

                    if (_options.Overflow != QueueOverflowBehavior.RejectPublish)
                        throw new MessageBusException("At-least-once dead-lettering requires overflow to be set to RejectPublish. Call .OverflowBehavior(QueueOverflowBehavior.RejectPublish).");
                }

                arguments["x-dead-letter-strategy"] = _options.DeadLetterStrategy.Value.ToEnumString();
            }
        }

        if (_options.Overflow.HasValue)
            arguments["x-overflow"] = _options.Overflow.Value.ToEnumString();

        if (_options.ConsumerTimeout.HasValue)
        {
            if (!_isQuorumQueue)
                throw new MessageBusException("Per-queue consumer timeout (x-consumer-timeout) requires quorum queues (RabbitMQ 4.3+). Call UseQuorumQueues() before ConsumerTimeout().");

            if (_serverVersion is not null && _serverVersion < _delayedExchangePluginIncompatibleVersion)
                throw new MessageBusException($"Per-queue consumer timeout (x-consumer-timeout) requires RabbitMQ 4.3+. Detected server version: {_serverVersion}.");

            arguments["x-consumer-timeout"] = (long)_options.ConsumerTimeout.Value.TotalMilliseconds;
        }

        if (_options.SingleActiveConsumer)
            arguments["x-single-active-consumer"] = true;

        if (_options.MaxPriority.HasValue)
            arguments["x-max-priority"] = (int)_options.MaxPriority.Value;

        if (_options.DelayedRetryType.HasValue)
        {
            if (!_isQuorumQueue)
                throw new MessageBusException("Delayed retries (x-delayed-retry-*) require quorum queues (RabbitMQ 4.3+). Call UseQuorumQueues() before UseDelayedRetries().");

            if (_serverVersion is not null && _serverVersion < _delayedExchangePluginIncompatibleVersion)
                throw new MessageBusException($"Delayed retries (x-delayed-retry-*) require RabbitMQ 4.3+. Detected server version: {_serverVersion}.");

            arguments["x-delayed-retry-type"] = _options.DelayedRetryType.Value.ToEnumString();
            if (_options.DelayedRetryMin.HasValue)
                arguments["x-delayed-retry-min"] = _options.DelayedRetryMin.Value;
            if (_options.DelayedRetryMax.HasValue)
                arguments["x-delayed-retry-max"] = _options.DelayedRetryMax.Value;
        }

        var result = await channel.QueueDeclareAsync(_options.SubscriptionQueueName, _options.IsDurable, _options.IsSubscriptionQueueExclusive, _options.SubscriptionQueueAutoDelete, arguments.Count > 0 ? arguments : null).AnyContext();
        string queueName = result.QueueName;

        // Bind the queue with the exchange.
        await channel.QueueBindAsync(queueName, _options.Topic, String.Empty).AnyContext();

        return queueName;
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

    private static string SanitizeUri(Uri uri)
    {
        if (String.IsNullOrEmpty(uri.UserInfo))
            return uri.ToString();

        string portSuffix = uri.IsDefaultPort ? "" : $":{uri.Port}";
        return $"{uri.Scheme}://***@{uri.Host}{portSuffix}{uri.AbsolutePath}";
    }

    /// <summary>
    /// Parses a host string in format "hostname" or "hostname:port" into an AmqpTcpEndpoint.
    /// </summary>
    private static AmqpTcpEndpoint? ParseHostEndpoint(string host, int defaultPort)
    {
        if (String.IsNullOrWhiteSpace(host))
            return null;

        string trimmed = host.Trim();

        // Handle IPv6 bracket notation: [::1] or [::1]:5672
        if (trimmed.StartsWith('['))
        {
            int closeBracket = trimmed.IndexOf(']');
            if (closeBracket < 0)
                return new AmqpTcpEndpoint(trimmed, defaultPort);

            string ipv6Host = trimmed[1..closeBracket];
            if (closeBracket + 1 < trimmed.Length && trimmed[closeBracket + 1] == ':')
            {
                return Int32.TryParse(trimmed[(closeBracket + 2)..], out int port)
                    ? new AmqpTcpEndpoint(ipv6Host, port)
                    : new AmqpTcpEndpoint(ipv6Host, defaultPort);
            }

            return new AmqpTcpEndpoint(ipv6Host, defaultPort);
        }

        int colonIndex = trimmed.LastIndexOf(':');
        if (colonIndex < 0)
            return new AmqpTcpEndpoint(trimmed, defaultPort);

        // Multiple colons without brackets indicates an unbracketed IPv6 address — treat as bare hostname
        if (trimmed.IndexOf(':') != colonIndex)
            return new AmqpTcpEndpoint(trimmed, defaultPort);

        string hostname = trimmed[..colonIndex];
        return Int32.TryParse(trimmed[(colonIndex + 1)..], out int parsedPort)
            ? new AmqpTcpEndpoint(hostname, parsedPort)
            : new AmqpTcpEndpoint(hostname, defaultPort);
    }

    private void RegisterPublisherConnectionEventHandlers()
    {
        if (_publisherConnection is null)
            throw new MessageBusException("Publisher connection must be initialized before registering event handlers.");

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
        if (_publisherConnection is null)
            throw new MessageBusException("Publisher connection must be initialized before unregistering event handlers.");

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
        if (_subscriberConnection is null)
            throw new MessageBusException("Subscriber connection must be initialized before registering event handlers.");

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
        if (_subscriberConnection is null)
            throw new MessageBusException("Subscriber connection must be initialized before unregistering event handlers.");

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
        if (_consumer is null)
            throw new MessageBusException("Consumer must be initialized before registering event handlers.");

        _consumer.ReceivedAsync += OnMessageAsync;
        _consumer.ShutdownAsync += OnConsumerShutdownAsync;
    }

    private void UnregisterConsumerEventHandlers()
    {
        if (_consumer is null)
            throw new MessageBusException("Consumer must be initialized before unregistering event handlers.");

        _consumer.ReceivedAsync -= OnMessageAsync;
        _consumer.ShutdownAsync -= OnConsumerShutdownAsync;
    }

    /// <summary>
    /// Simulates an unexpected publisher connection loss for testing the recovery gate.
    /// Closes the gate so that subsequent publishes will wait for recovery.
    /// </summary>
    internal void SimulatePublisherConnectionLost()
    {
        _publisherReady.Reset();
    }

    /// <summary>
    /// Simulates a successful publisher connection recovery for testing.
    /// Opens the gate so that waiting publishes resume.
    /// </summary>
    internal void SimulatePublisherRecoverySucceeded()
    {
        _publisherReady.Set();
    }

    /// <summary>
    /// Simulates a publisher connection shutdown event for testing.
    /// Triggers the full shutdown handler (gate closure) and nulls the publisher channel
    /// to represent a real unexpected disconnect.
    /// </summary>
    internal async Task SimulatePublisherConnectionShutdownAsync()
    {
        await OnPublisherConnectionOnConnectionShutdownAsync(this,
            new ShutdownEventArgs(ShutdownInitiator.Library, 541, "Simulated connection reset")).AnyContext();
        _publisherChannel = null;
    }

    /// <summary>
    /// Simulates a connection recovery error event for testing.
    /// Verifies the gate remains closed (recovery continues retrying).
    /// </summary>
    internal Task SimulatePublisherConnectionRecoveryErrorAsync()
    {
        return OnPublisherConnectionOnConnectionRecoveryErrorAsync(this,
            new ConnectionRecoveryErrorEventArgs(new Exception("Simulated recovery failure")));
    }
}

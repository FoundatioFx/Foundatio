using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Foundatio.AsyncEx;
using Foundatio.AzureServiceBus.Utility;
using Foundatio.Extensions;
using Foundatio.Serializer;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging;

public class AzureServiceBusMessageBus : MessageBusBase<AzureServiceBusMessageBusOptions>
{
    private readonly AsyncLock _lock = new();
    private readonly Lazy<ServiceBusClient> _client;
    private readonly Lazy<ServiceBusAdministrationClient> _adminClient;
    private readonly bool _isEmulator;
    private ServiceBusSender _topicSender;
    private ServiceBusProcessor _subscriptionProcessor;
    private readonly string _subscriptionName;

    public AzureServiceBusMessageBus(AzureServiceBusMessageBusOptions options) : base(options)
    {
        if (String.IsNullOrEmpty(options.ConnectionString) && String.IsNullOrEmpty(options.FullyQualifiedNamespace))
            throw new ArgumentException("ConnectionString or FullyQualifiedNamespace is required.");

        if (!String.IsNullOrEmpty(options.FullyQualifiedNamespace) && options.Credential == null)
            throw new ArgumentException("Credential is required when using FullyQualifiedNamespace.");

        _subscriptionName = _options.SubscriptionName ?? MessageBusId;

        // Detect if using the Azure Service Bus Emulator
        _isEmulator = !String.IsNullOrEmpty(options.ConnectionString) &&
            options.ConnectionString.Contains("UseDevelopmentEmulator=true", StringComparison.OrdinalIgnoreCase);

        _client = new Lazy<ServiceBusClient>(() =>
        {
            if (!String.IsNullOrEmpty(options.ConnectionString))
                return new ServiceBusClient(options.ConnectionString);

            return new ServiceBusClient(options.FullyQualifiedNamespace, options.Credential);
        });

        _adminClient = new Lazy<ServiceBusAdministrationClient>(() =>
        {
            if (!String.IsNullOrEmpty(options.ConnectionString))
                return new ServiceBusAdministrationClient(options.ConnectionString);

            return new ServiceBusAdministrationClient(options.FullyQualifiedNamespace, options.Credential);
        });
    }

    public AzureServiceBusMessageBus(
        Builder<AzureServiceBusMessageBusOptionsBuilder, AzureServiceBusMessageBusOptions> config)
        : this(config(new AzureServiceBusMessageBusOptionsBuilder()).Build())
    {
    }

    public ServiceBusClient Client => _client.Value;
    public ServiceBusAdministrationClient AdminClient => _adminClient.Value;

    protected override async Task EnsureTopicSubscriptionAsync(CancellationToken cancellationToken)
    {
        if (_subscriptionProcessor != null)
            return;

        if (!TopicIsCreated)
            await EnsureTopicCreatedAsync(cancellationToken).AnyContext();

        using (await _lock.LockAsync(cancellationToken).AnyContext())
        {
            if (_subscriptionProcessor != null)
                return;

            _logger.LogTrace("Ensuring subscription {SubscriptionName} exists on topic {Topic}", _subscriptionName, _options.Topic);

            // Skip admin API calls when using the emulator - it doesn't support the management HTTP API
            if (!_isEmulator)
            {
                try
                {
                    bool subscriptionExists = await _adminClient.Value.SubscriptionExistsAsync(_options.Topic, _subscriptionName, cancellationToken).AnyContext();
                    if (!subscriptionExists)
                    {
                        if (!_options.CanCreateTopic)
                            throw new InvalidOperationException($"Subscription {_subscriptionName} does not exist on topic {_options.Topic} and CanCreateTopic is false.");

                        await _adminClient.Value.CreateSubscriptionAsync(CreateSubscriptionOptions(), cancellationToken).AnyContext();
                        _logger.LogDebug("Created subscription {SubscriptionName} on topic {Topic}", _subscriptionName, _options.Topic);
                    }
                }
                catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityAlreadyExists)
                {
                    _logger.LogDebug(ex, "Subscription {SubscriptionName} already exists on topic {Topic}", _subscriptionName, _options.Topic);
                }
            }
            else
            {
                _logger.LogDebug("Skipping subscription existence check - using Azure Service Bus Emulator");
            }

            var processorOptions = new ServiceBusProcessorOptions
            {
                AutoCompleteMessages = true,
                MaxConcurrentCalls = _options.MaxConcurrentCalls,
                ReceiveMode = _options.SubscriptionReceiveMode
            };

            if (_options.PrefetchCount.HasValue)
                processorOptions.PrefetchCount = _options.PrefetchCount.Value;

            _subscriptionProcessor = _client.Value.CreateProcessor(_options.Topic, _subscriptionName, processorOptions);
            _subscriptionProcessor.ProcessMessageAsync += OnMessageAsync;
            _subscriptionProcessor.ProcessErrorAsync += OnErrorAsync;

            await _subscriptionProcessor.StartProcessingAsync(cancellationToken).AnyContext();
            _logger.LogTrace("Subscription processor started for {SubscriptionName} on topic {Topic}", _subscriptionName, _options.Topic);
        }
    }

    private async Task OnMessageAsync(ProcessMessageEventArgs args)
    {
        if (_subscribers.IsEmpty)
            return;

        var brokeredMessage = args.Message;
        using var _ = _logger.BeginScope(s => s
            .Property("MessageId", brokeredMessage.MessageId));

        _logger.LogTrace("OnMessageAsync({MessageId})", brokeredMessage.MessageId);

        var message = new Message(brokeredMessage.Body.ToArray(), DeserializeMessageBody)
        {
            Type = brokeredMessage.ContentType,
            ClrType = GetMappedMessageType(brokeredMessage.ContentType),
            CorrelationId = brokeredMessage.CorrelationId,
            UniqueId = brokeredMessage.MessageId
        };

        foreach (var property in brokeredMessage.ApplicationProperties)
        {
            // Filter out Azure Service Bus SDK diagnostic properties that are automatically added
            if (ServiceBusMessageHelper.IsSdkDiagnosticProperty(property.Key))
                continue;

            message.Properties[property.Key] = property.Value?.ToString();
        }

        try
        {
            await SendMessageToSubscribersAsync(message).AnyContext();
        }
        catch (MessageBusException)
        {
            // SendMessageToSubscribersAsync already logged the error
            // Azure Service Bus SDK will handle retry/dead-letter based on MaxDeliveryCount
        }
        catch (Exception ex)
        {
            // Catch any other unexpected exceptions for defensive purposes
            // Azure Service Bus SDK will handle retry/dead-letter based on MaxDeliveryCount
            _logger.LogError(ex, "OnMessageAsync({MessageId}) Error in subscriber: {Message}", brokeredMessage.MessageId, ex.Message);
        }
    }

    private Task OnErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogWarning(args.Exception, "Message processing error: {Message} EntityPath={EntityPath}", args.Exception.Message, args.EntityPath);
        return Task.CompletedTask;
    }

    private bool TopicIsCreated => _topicSender != null;

    protected override async Task EnsureTopicCreatedAsync(CancellationToken cancellationToken)
    {
        if (TopicIsCreated)
            return;

        using (await _lock.LockAsync(cancellationToken).AnyContext())
        {
            if (TopicIsCreated)
                return;

            _logger.LogTrace("Ensuring topic {Topic} exists", _options.Topic);

            // Skip admin API calls when using the emulator - it doesn't support the management HTTP API
            if (!_isEmulator)
            {
                try
                {
                    bool topicExists = await _adminClient.Value.TopicExistsAsync(_options.Topic, cancellationToken).AnyContext();
                    if (!topicExists)
                    {
                        if (!_options.CanCreateTopic)
                            throw new InvalidOperationException($"Topic {_options.Topic} does not exist and CanCreateTopic is false.");

                        await _adminClient.Value.CreateTopicAsync(CreateTopicOptions(), cancellationToken).AnyContext();
                        _logger.LogDebug("Created topic {Topic}", _options.Topic);
                    }
                }
                catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityAlreadyExists)
                {
                    _logger.LogDebug("Topic {Topic} already exists", _options.Topic);
                }
            }
            else
            {
                _logger.LogDebug("Skipping topic existence check - using Azure Service Bus Emulator");
            }

            _topicSender = _client.Value.CreateSender(_options.Topic);
            _logger.LogTrace("Topic sender created for {Topic}", _options.Topic);
        }
    }

    protected override async Task PublishImplAsync(string messageType, object message, MessageOptions options, CancellationToken cancellationToken)
    {
        var serviceBusMessage = new ServiceBusMessage(_serializer.SerializeToBytes(message))
        {
            CorrelationId = options.CorrelationId,
            ContentType = messageType
        };

        if (!String.IsNullOrEmpty(options.UniqueId))
            serviceBusMessage.MessageId = options.UniqueId;

        if (options.Properties is not null)
        {
            foreach (var property in options.Properties)
                serviceBusMessage.ApplicationProperties[property.Key] = property.Value;
        }

        if (options.DeliveryDelay.HasValue && options.DeliveryDelay.Value > TimeSpan.Zero)
        {
            _logger.LogTrace("Schedule delayed message: {MessageType} ({Delay}ms)", messageType, options.DeliveryDelay.Value.TotalMilliseconds);
            serviceBusMessage.ScheduledEnqueueTime = _timeProvider.GetUtcNow().UtcDateTime.Add(options.DeliveryDelay.Value);
        }
        else
        {
            _logger.LogTrace("Message Publish: {MessageType}", messageType);
        }

        // Wrap only the transport call in resilience policy
        await _resiliencePolicy.ExecuteAsync(async _ =>
            await _topicSender.SendMessageAsync(serviceBusMessage, cancellationToken),
            cancellationToken).AnyContext();
    }

    private CreateTopicOptions CreateTopicOptions()
    {
        var options = new CreateTopicOptions(_options.Topic);

        if (_options.TopicAutoDeleteOnIdle.HasValue)
            options.AutoDeleteOnIdle = _options.TopicAutoDeleteOnIdle.Value;

        if (_options.TopicDefaultMessageTimeToLive.HasValue)
            options.DefaultMessageTimeToLive = _options.TopicDefaultMessageTimeToLive.Value;

        if (_options.TopicMaxSizeInMegabytes.HasValue)
            options.MaxSizeInMegabytes = _options.TopicMaxSizeInMegabytes.Value;

        if (_options.TopicRequiresDuplicateDetection.HasValue)
            options.RequiresDuplicateDetection = _options.TopicRequiresDuplicateDetection.Value;

        if (_options.TopicDuplicateDetectionHistoryTimeWindow.HasValue)
            options.DuplicateDetectionHistoryTimeWindow = _options.TopicDuplicateDetectionHistoryTimeWindow.Value;

        if (_options.TopicEnableBatchedOperations.HasValue)
            options.EnableBatchedOperations = _options.TopicEnableBatchedOperations.Value;

        if (_options.TopicStatus.HasValue)
            options.Status = _options.TopicStatus.Value;

        if (_options.TopicSupportOrdering.HasValue)
            options.SupportOrdering = _options.TopicSupportOrdering.Value;

        if (_options.TopicEnablePartitioning.HasValue)
            options.EnablePartitioning = _options.TopicEnablePartitioning.Value;

        if (!String.IsNullOrEmpty(_options.TopicUserMetadata))
            options.UserMetadata = _options.TopicUserMetadata;

        return options;
    }

    private CreateSubscriptionOptions CreateSubscriptionOptions()
    {
        var options = new CreateSubscriptionOptions(_options.Topic, _subscriptionName);

        if (_options.SubscriptionAutoDeleteOnIdle.HasValue)
            options.AutoDeleteOnIdle = _options.SubscriptionAutoDeleteOnIdle.Value;

        if (_options.SubscriptionDefaultMessageTimeToLive.HasValue)
            options.DefaultMessageTimeToLive = _options.SubscriptionDefaultMessageTimeToLive.Value;

        if (_options.SubscriptionLockDuration.HasValue)
            options.LockDuration = _options.SubscriptionLockDuration.Value;

        if (_options.SubscriptionRequiresSession.HasValue)
            options.RequiresSession = _options.SubscriptionRequiresSession.Value;

        if (_options.SubscriptionEnableDeadLetteringOnMessageExpiration.HasValue)
            options.DeadLetteringOnMessageExpiration = _options.SubscriptionEnableDeadLetteringOnMessageExpiration.Value;

        if (_options.SubscriptionEnableDeadLetteringOnFilterEvaluationExceptions.HasValue)
            options.EnableDeadLetteringOnFilterEvaluationExceptions = _options.SubscriptionEnableDeadLetteringOnFilterEvaluationExceptions.Value;

        if (_options.SubscriptionMaxDeliveryCount.HasValue)
            options.MaxDeliveryCount = _options.SubscriptionMaxDeliveryCount.Value;

        if (_options.SubscriptionEnableBatchedOperations.HasValue)
            options.EnableBatchedOperations = _options.SubscriptionEnableBatchedOperations.Value;

        if (_options.SubscriptionStatus.HasValue)
            options.Status = _options.SubscriptionStatus.Value;

        if (!String.IsNullOrEmpty(_options.SubscriptionForwardTo))
            options.ForwardTo = _options.SubscriptionForwardTo;

        if (!String.IsNullOrEmpty(_options.SubscriptionForwardDeadLetteredMessagesTo))
            options.ForwardDeadLetteredMessagesTo = _options.SubscriptionForwardDeadLetteredMessagesTo;

        if (!String.IsNullOrEmpty(_options.SubscriptionUserMetadata))
            options.UserMetadata = _options.SubscriptionUserMetadata;

        return options;
    }

    public override void Dispose()
    {
        // TODO: Improve Async Cleanup
        base.Dispose();
        CloseTopicSender();
        CloseSubscriptionProcessor();

        if (_client.IsValueCreated)
        {
            _client.Value.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private void CloseTopicSender()
    {
        if (_topicSender == null)
            return;

        using (_lock.Lock())
        {
            // Double-check after acquiring lock (another thread may have closed it)
            if (_topicSender == null)
                return;

            _topicSender.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _topicSender = null;
        }
    }

    private void CloseSubscriptionProcessor()
    {
        if (_subscriptionProcessor == null)
            return;

        using (_lock.Lock())
        {
            // Double-check after acquiring lock (another thread may have closed it)
            if (_subscriptionProcessor == null)
                return;

            _subscriptionProcessor.StopProcessingAsync().GetAwaiter().GetResult();
            _subscriptionProcessor.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _subscriptionProcessor = null;
        }
    }
}

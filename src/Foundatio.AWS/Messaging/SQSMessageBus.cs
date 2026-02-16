using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.Credentials;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Foundatio.AsyncEx;
using Foundatio.Extensions;
using Foundatio.Serializer;
using Microsoft.Extensions.Logging;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;
using SnsNotFoundException = Amazon.SimpleNotificationService.Model.NotFoundException;

namespace Foundatio.Messaging;

/// <summary>
/// AWS SNS/SQS message bus implementation that uses SNS (Simple Notification Service) for publishing
/// and SQS (Simple Queue Service) for subscribing.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Architecture:</strong> This implementation uses the SNS fan-out pattern where messages are
/// published to an SNS topic and delivered to SQS queues subscribed to that topic. Each subscriber
/// gets its own SQS queue, enabling true pub/sub semantics where all subscribers receive all messages.
/// </para>
/// <para>
/// <strong>Queue Management:</strong> By default, each message bus instance creates a unique SQS queue
/// with a randomly generated name. For durable subscriptions that persist across restarts, use
/// <see cref="SQSMessageBusOptions.SubscriptionQueueName"/> to specify a fixed queue name.
/// </para>
/// <para>
/// <strong>Policy Management:</strong> When subscribing, the implementation automatically configures
/// the SQS queue policy to allow the SNS topic to send messages. Policies are merged rather than
/// replaced, preserving any existing permissions. This is important for durable queues that may
/// be reused across restarts or shared between processes.
/// </para>
/// <para>
/// <strong>Resource Optimization:</strong> The implementation checks for existing queues and policies
/// before creating or updating them, minimizing unnecessary AWS API calls. This is especially important
/// in multi-process scenarios where multiple instances may start simultaneously.
/// </para>
/// <para>
/// <strong>Cleanup:</strong> By default, SQS queues are automatically deleted when the message bus is
/// disposed (<see cref="SQSMessageBusOptions.SubscriptionQueueAutoDelete"/>). For durable subscriptions,
/// set this to false to preserve the queue.
/// </para>
/// <para>
/// <strong>Delayed Messages:</strong> Delayed message delivery is handled in-memory using timers from
/// the base class, not SQS delay queues. This provides sub-second precision but means delayed messages
/// are lost if the process restarts before delivery.
/// </para>
/// </remarks>
/// <seealso cref="SQSMessageBusOptions"/>
/// <seealso cref="SQSMessageBusOptionsBuilder"/>
public class SQSMessageBus : MessageBusBase<SQSMessageBusOptions>, IAsyncDisposable
{
    private readonly AsyncLock _lock = new();
    private readonly Lazy<AmazonSimpleNotificationServiceClient> _snsClient;
    private readonly Lazy<AmazonSQSClient> _sqsClient;
    private readonly ConcurrentDictionary<string, string> _topicArns = new();
    private string _queueUrl;
    private string _queueArn;
    private string _subscriptionArn;
    private CancellationTokenSource _subscriberCts;
    private Task _subscriberTask;
    private bool _isDisposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SQSMessageBus"/> class with the specified options.
    /// </summary>
    /// <param name="options">The configuration options for the message bus.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <see cref="SQSMessageBusOptions.SubscriptionQueueName"/> is invalid.</exception>
    public SQSMessageBus(SQSMessageBusOptions options) : base(options)
    {
        // Validate queue name early to fail fast
        if (!String.IsNullOrEmpty(options.SubscriptionQueueName))
            ValidateQueueName(options.SubscriptionQueueName, nameof(options.SubscriptionQueueName));

        var credentials = options.Credentials ?? DefaultAWSCredentialsIdentityResolver.GetCredentials();
        var region = options.Region ?? FallbackRegionFactory.GetRegionEndpoint();

        _snsClient = new Lazy<AmazonSimpleNotificationServiceClient>(() =>
        {
            if (String.IsNullOrEmpty(options.ServiceUrl))
                return new AmazonSimpleNotificationServiceClient(credentials, region);

            return new AmazonSimpleNotificationServiceClient(credentials, new AmazonSimpleNotificationServiceConfig
            {
                RegionEndpoint = RegionEndpoint.USEast1,
                ServiceURL = options.ServiceUrl
            });
        });

        _sqsClient = new Lazy<AmazonSQSClient>(() =>
        {
            if (String.IsNullOrEmpty(options.ServiceUrl))
                return new AmazonSQSClient(credentials, region);

            return new AmazonSQSClient(credentials, new AmazonSQSConfig
            {
                RegionEndpoint = RegionEndpoint.USEast1,
                ServiceURL = options.ServiceUrl
            });
        });
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SQSMessageBus"/> class using a builder pattern.
    /// </summary>
    /// <param name="builder">A function that configures the options builder.</param>
    public SQSMessageBus(Builder<SQSMessageBusOptionsBuilder, SQSMessageBusOptions> builder)
        : this(builder(new SQSMessageBusOptionsBuilder()).Build()) { }

    /// <summary>
    /// Gets the underlying Amazon SNS client used for publishing messages.
    /// </summary>
    /// <remarks>
    /// The client is lazily initialized on first access. Use this property to perform
    /// advanced SNS operations not exposed by the message bus interface.
    /// </remarks>
    public AmazonSimpleNotificationServiceClient SnsClient => _snsClient.Value;

    /// <summary>
    /// Gets the underlying Amazon SQS client used for receiving messages.
    /// </summary>
    /// <remarks>
    /// The client is lazily initialized on first access. Use this property to perform
    /// advanced SQS operations not exposed by the message bus interface.
    /// </remarks>
    public AmazonSQSClient SqsClient => _sqsClient.Value;

    /// <summary>
    /// Ensures the SNS topic exists, creating it if necessary and permitted.
    /// </summary>
    /// <remarks>
    /// This method first attempts to find an existing topic by name. If not found and
    /// <see cref="SQSMessageBusOptions.CanCreateTopic"/> is true, it creates the topic.
    /// The operation is thread-safe and idempotent.
    /// </remarks>
    protected override async Task EnsureTopicCreatedAsync(CancellationToken cancellationToken)
    {
        await GetOrCreateTopicArnAsync(_options.Topic, cancellationToken).AnyContext();
    }

    /// <summary>
    /// Gets the topic name for a given message type using the configured TopicResolver.
    /// </summary>
    /// <param name="messageType">The CLR type of the message.</param>
    /// <returns>The resolved topic name, or the default topic if no resolver is configured or it returns null.</returns>
    private string GetTopicName(Type messageType)
    {
        if (_options.TopicResolver is null)
            return _options.Topic;

        return _options.TopicResolver(messageType) ?? _options.Topic;
    }

    /// <summary>
    /// Gets or creates the SNS topic ARN for the specified topic name, with memoization.
    /// </summary>
    /// <param name="topicName">The name of the SNS topic.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The ARN of the SNS topic.</returns>
    /// <remarks>
    /// Topic ARNs are cached in a ConcurrentDictionary to avoid redundant AWS API calls.
    /// Caller must hold the lock when topic creation may occur.
    /// </remarks>
    private async Task<string> GetOrCreateTopicArnAsync(string topicName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(topicName, nameof(topicName));

        if (_topicArns.TryGetValue(topicName, out string arn))
            return arn;

        arn = await CreateTopicImplAsync(topicName, cancellationToken).AnyContext();
        _topicArns.TryAdd(topicName, arn);
        return arn;
    }

    /// <summary>
    /// Internal implementation of topic creation. Caller must hold the lock.
    /// </summary>
    private async Task<string> CreateTopicImplAsync(string topicName, CancellationToken cancellationToken)
    {
        _logger.LogTrace("Ensuring SNS topic {Topic} exists", topicName);

        if (!_options.CanCreateTopic)
        {
            // Only check if topic exists when we can't create it
            try
            {
                var findResponse = await _snsClient.Value.FindTopicAsync(topicName).AnyContext();
                if (findResponse?.TopicArn is not null)
                {
                    _logger.LogDebug("Found existing SNS topic {Topic} with ARN {TopicArn}", topicName, findResponse.TopicArn);
                    return findResponse.TopicArn;
                }
            }
            catch (SnsNotFoundException)
            {
                // Topic not found and we can't create it
            }
            catch (AmazonServiceException ex)
            {
                _logger.LogWarning(ex, "Error finding topic {Topic}", topicName);
            }

            throw new MessageBusException($"Topic {topicName} does not exist and CanCreateTopic is false.");
        }

        // CreateTopicAsync is idempotent - if topic exists, it returns the existing ARN
        // This is much faster than FindTopicAsync which lists all topics
        try
        {
            var createResponse = await _snsClient.Value.CreateTopicAsync(new CreateTopicRequest
            {
                Name = topicName
            }, cancellationToken).AnyContext();

            _logger.LogDebug("Ensured SNS topic {Topic} exists with ARN {TopicArn}", topicName, createResponse.TopicArn);
            return createResponse.TopicArn;
        }
        catch (AmazonServiceException ex)
        {
            throw new MessageBusException($"Failed to create SNS topic {topicName}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Ensures an SQS queue is subscribed to the SNS topic and starts the message polling loop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method performs the following operations:
    /// <list type="number">
    /// <item>Checks if the SQS queue already exists (to avoid redundant CreateQueue calls)</item>
    /// <item>Creates the queue if it doesn't exist, with configured encryption and visibility settings</item>
    /// <item>Retrieves the queue ARN and existing policy</item>
    /// <item>Updates the queue policy only if the SNS topic permission is not already present</item>
    /// <item>Subscribes the queue to the SNS topic with raw message delivery enabled</item>
    /// <item>Starts the background message polling loop</item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>Multi-Process Safety:</strong> The implementation is designed to minimize redundant AWS API
    /// calls when multiple processes start simultaneously. It checks for existing queues and policies before
    /// creating or updating them.
    /// </para>
    /// <para>
    /// <strong>Policy Merging:</strong> Queue policies are merged rather than replaced, preserving any
    /// existing permissions. Each SNS topic gets a unique statement ID based on its ARN hash, allowing
    /// the same queue to receive messages from multiple topics if needed.
    /// </para>
    /// </remarks>
    protected override async Task EnsureTopicSubscriptionAsync(CancellationToken cancellationToken)
    {
        if (_subscriberTask is not null)
            return;

        using (await _lock.LockAsync(cancellationToken).AnyContext())
        {
            if (_subscriberTask is not null)
                return;

            // Get or create the default topic for subscription
            string topicArn = await GetOrCreateTopicArnAsync(_options.Topic, cancellationToken).AnyContext();

            string queueName = GetSubscriptionQueueName();
            _logger.LogTrace("Ensuring SQS queue {QueueName} exists for subscription", queueName);

            try
            {
                // First, try to get the existing queue URL to avoid unnecessary CreateQueue calls
                bool queueExists = false;
                try
                {
                    var urlResponse = await _sqsClient.Value.GetQueueUrlAsync(queueName, cancellationToken).AnyContext();
                    _queueUrl = urlResponse.QueueUrl;
                    queueExists = true;
                    _logger.LogDebug("Found existing SQS queue {QueueName} with URL {QueueUrl}", queueName, _queueUrl);
                }
                catch (QueueDoesNotExistException)
                {
                    _logger.LogTrace("Queue {QueueName} not found, will create", queueName);
                }
                catch (AmazonServiceException ex)
                {
                    // Log as warning and continue to create - the create call will fail if there's a real problem
                    _logger.LogWarning(ex, "Error checking if queue {QueueName} exists, will attempt to create", queueName);
                }

                if (!queueExists)
                {
                    var createQueueRequest = new CreateQueueRequest
                    {
                        QueueName = queueName,
                        Attributes = new Dictionary<string, string>()
                    };

                    if (_options.SqsManagedSseEnabled)
                    {
                        createQueueRequest.Attributes[QueueAttributeName.SqsManagedSseEnabled] = "true";
                    }
                    else if (!String.IsNullOrEmpty(_options.KmsMasterKeyId))
                    {
                        createQueueRequest.Attributes[QueueAttributeName.KmsMasterKeyId] = _options.KmsMasterKeyId;
                        createQueueRequest.Attributes[QueueAttributeName.KmsDataKeyReusePeriodSeconds] = _options.KmsDataKeyReusePeriodSeconds.ToString();
                    }

                    if (_options.MessageVisibilityTimeout.HasValue)
                    {
                        createQueueRequest.Attributes[QueueAttributeName.VisibilityTimeout] =
                            ((int)_options.MessageVisibilityTimeout.Value.TotalSeconds).ToString();
                    }

                    var createQueueResponse = await _sqsClient.Value.CreateQueueAsync(createQueueRequest, cancellationToken).AnyContext();
                    _queueUrl = createQueueResponse.QueueUrl;
                    _logger.LogDebug("Created SQS queue {QueueName} with URL {QueueUrl}", queueName, _queueUrl);
                }

                var getAttributesResponse = await _sqsClient.Value.GetQueueAttributesAsync(new GetQueueAttributesRequest
                {
                    QueueUrl = _queueUrl,
                    AttributeNames = [QueueAttributeName.QueueArn, QueueAttributeName.Policy]
                }, cancellationToken).AnyContext();

                _queueArn = getAttributesResponse.QueueARN;

                // Check if policy already allows this SNS topic
                string expectedSid = $"AllowSNS-{topicArn.GetHashCode():X8}";
                bool policyNeedsUpdate = !PolicyContainsStatement(getAttributesResponse.Policy, expectedSid);

                if (policyNeedsUpdate)
                {
                    string policy = GetMergedQueuePolicy(getAttributesResponse.Policy, _queueArn, topicArn);

                    await _sqsClient.Value.SetQueueAttributesAsync(new SetQueueAttributesRequest
                    {
                        QueueUrl = _queueUrl,
                        Attributes = new Dictionary<string, string>
                        {
                            [QueueAttributeName.Policy] = policy
                        }
                    }, cancellationToken).AnyContext();

                    _logger.LogDebug("Updated SQS queue policy to allow SNS topic {TopicArn}", topicArn);
                }

                var subscribeResponse = await _snsClient.Value.SubscribeAsync(new SubscribeRequest
                {
                    TopicArn = topicArn,
                    Protocol = "sqs",
                    Endpoint = _queueArn,
                    Attributes = new Dictionary<string, string>
                    {
                        ["RawMessageDelivery"] = "true"
                    }
                }, cancellationToken).AnyContext();

                _subscriptionArn = subscribeResponse.SubscriptionArn;
                _logger.LogDebug("Subscribed SQS queue {QueueArn} to SNS topic {TopicArn} with subscription {SubscriptionArn}", _queueArn, topicArn, _subscriptionArn);

                _subscriberCts = CancellationTokenSource.CreateLinkedTokenSource(DisposedCancellationToken);
                _subscriberTask = Task.Run(() => SubscriberLoopAsync(_subscriberCts.Token), _subscriberCts.Token);
            }
            catch (QueueNameExistsException ex)
            {
                throw new MessageBusException($"SQS queue {queueName} already exists with different attributes: {ex.Message}", ex);
            }
            catch (AmazonServiceException ex)
            {
                throw new MessageBusException($"Failed to create SQS subscription for topic {_options.Topic}: {ex.Message}", ex);
            }
        }
    }

    protected override async Task RemoveTopicSubscriptionAsync()
    {
        using (await _lock.LockAsync(DisposedCancellationToken).AnyContext())
        {
            // Cancel the subscriber first
            if (_subscriberCts is not null)
                await _subscriberCts.CancelAsync().AnyContext();

            // Wait for the subscriber task to complete before disposing the CTS
            if (_subscriberTask is not null)
            {
                try
                {
                    await _subscriberTask.AnyContext();
                }
                catch (OperationCanceledException)
                {
                    // Expected when the subscriber is canceled during shutdown
                }
                _subscriberTask = null;
            }

            // Now safe to dispose the CTS
            if (_subscriberCts is not null)
            {
                _subscriberCts.Dispose();
                _subscriberCts = null;
            }

            if (!String.IsNullOrEmpty(_subscriptionArn))
            {
                try
                {
                    await _snsClient.Value.UnsubscribeAsync(_subscriptionArn).AnyContext();
                    _logger.LogDebug("Unsubscribed from SNS topic: {SubscriptionArn}", _subscriptionArn);
                }
                catch (SnsNotFoundException)
                {
                    _logger.LogDebug("SNS subscription {SubscriptionArn} not found during cleanup", _subscriptionArn);
                }
                catch (AmazonServiceException ex)
                {
                    _logger.LogWarning(ex, "Error unsubscribing from SNS topic: {Message}", ex.Message);
                }
                _subscriptionArn = null;
            }

            if (!String.IsNullOrEmpty(_queueUrl) && _options.SubscriptionQueueAutoDelete)
            {
                try
                {
                    await _sqsClient.Value.DeleteQueueAsync(_queueUrl).AnyContext();
                    _logger.LogDebug("Deleted SQS queue: {QueueUrl}", _queueUrl);
                }
                catch (QueueDoesNotExistException)
                {
                    _logger.LogDebug("SQS queue {QueueUrl} not found during cleanup", _queueUrl);
                }
                catch (AmazonServiceException ex)
                {
                    _logger.LogWarning(ex, "Error deleting SQS queue: {Message}", ex.Message);
                }
            }

            _queueUrl = null;
            _queueArn = null;
        }
    }

    private async Task SubscriberLoopAsync(CancellationToken cancellationToken)
    {
        _logger.LogTrace("Starting subscriber loop for {MessageBusId}", MessageBusId);

        int waitTimeout = (int)Math.Round(_options.ReadQueueTimeout.TotalSeconds, MidpointRounding.AwayFromZero);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var request = new ReceiveMessageRequest
                {
                    QueueUrl = _queueUrl,
                    MaxNumberOfMessages = 10,
                    WaitTimeSeconds = waitTimeout,
                    MessageSystemAttributeNames = ["All"],
                    MessageAttributeNames = ["All"]
                };

                var response = await _sqsClient.Value.ReceiveMessageAsync(request, cancellationToken).AnyContext();

                if (response?.Messages is null || response.Messages.Count == 0)
                {
                    if (_options.DequeueInterval > TimeSpan.Zero && !cancellationToken.IsCancellationRequested)
                    {
                        try
                        {
                            await _timeProvider.Delay(_options.DequeueInterval, cancellationToken).AnyContext();
                        }
                        catch (OperationCanceledException)
                        {
                            // Ignored
                        }
                    }

                    continue;
                }

                foreach (var sqsMessage in response.Messages)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    try
                    {
                        await ProcessMessageAsync(sqsMessage, cancellationToken).AnyContext();

                        await _sqsClient.Value.DeleteMessageAsync(new DeleteMessageRequest
                        {
                            QueueUrl = _queueUrl,
                            ReceiptHandle = sqsMessage.ReceiptHandle
                        }, CancellationToken.None).AnyContext();
                    }
                    catch (ReceiptHandleIsInvalidException ex)
                    {
                        _logger.LogWarning(ex, "Receipt handle invalid for message {MessageId}, message may have been deleted", sqsMessage.MessageId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing message {MessageId}: {Message}", sqsMessage.MessageId, ex.Message);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (QueueDoesNotExistException ex)
            {
                _logger.LogError(ex, "SQS queue no longer exists, stopping subscriber loop");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in subscriber loop: {Message}", ex.Message);
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    await _timeProvider.Delay(TimeSpan.FromSeconds(1), cancellationToken).AnyContext();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _logger.LogTrace("Subscriber loop ended for {MessageBusId}", MessageBusId);
    }

    private async Task ProcessMessageAsync(Amazon.SQS.Model.Message sqsMessage, CancellationToken cancellationToken)
    {
        if (_subscribers.IsEmpty)
            return;

        _logger.LogTrace("Processing message {MessageId}", sqsMessage.MessageId);

        string messageType = null;
        string correlationId = null;
        string uniqueId = sqsMessage.MessageId;
        var properties = new Dictionary<string, string>();

        if (sqsMessage.MessageAttributes is not null)
        {
            foreach (var attr in sqsMessage.MessageAttributes)
            {
                if (String.Equals(attr.Key, "MessageType", StringComparison.OrdinalIgnoreCase))
                    messageType = attr.Value.StringValue;
                else if (String.Equals(attr.Key, "CorrelationId", StringComparison.OrdinalIgnoreCase))
                    correlationId = attr.Value.StringValue;
                else if (String.Equals(attr.Key, "UniqueId", StringComparison.OrdinalIgnoreCase))
                    uniqueId = attr.Value.StringValue;
                else
                    properties[attr.Key] = attr.Value.StringValue;
            }
        }

        byte[] messageData = Encoding.UTF8.GetBytes(sqsMessage.Body);
        var message = new Message(messageData, DeserializeMessageBody)
        {
            Type = messageType,
            ClrType = GetMappedMessageType(messageType),
            CorrelationId = correlationId,
            UniqueId = uniqueId
        };

        foreach (var property in properties)
            message.Properties[property.Key] = property.Value;

        await SendMessageToSubscribersAsync(message).AnyContext();
    }

    protected override async Task PublishImplAsync(string messageType, object message, MessageOptions options, CancellationToken cancellationToken)
    {
        if (options.DeliveryDelay.HasValue && options.DeliveryDelay.Value > TimeSpan.Zero)
        {
            _logger.LogTrace("Scheduling delayed message: {MessageType} ({Delay}ms)", messageType, options.DeliveryDelay.Value.TotalMilliseconds);
            var clrType = GetMappedMessageType(messageType);
            if (clrType is not null)
                SendDelayedMessage(clrType, message, options);
            else
                _logger.LogWarning("Cannot schedule delayed message: unable to resolve CLR type for {MessageType}", messageType);
            return;
        }

        // Resolve the topic name based on message type
        Type clrMessageType = GetMappedMessageType(messageType);
        string topicName = clrMessageType is not null ? GetTopicName(clrMessageType) : _options.Topic;
        string topicArn = await GetOrCreateTopicArnAsync(topicName, cancellationToken).AnyContext();

        _logger.LogTrace("Publishing message: {MessageType} to topic {Topic}", messageType, topicName);

        string messageBody = _serializer.SerializeToString(message);
        var publishRequest = new PublishRequest
        {
            TopicArn = topicArn,
            Message = messageBody,
            MessageAttributes = new Dictionary<string, SnsMessageAttributeValue>
            {
                ["MessageType"] = new()
                {
                    DataType = "String",
                    StringValue = messageType
                }
            }
        };

        if (!String.IsNullOrEmpty(options.CorrelationId))
        {
            publishRequest.MessageAttributes["CorrelationId"] = new SnsMessageAttributeValue
            {
                DataType = "String",
                StringValue = options.CorrelationId
            };
        }

        if (!String.IsNullOrEmpty(options.UniqueId))
        {
            publishRequest.MessageAttributes["UniqueId"] = new SnsMessageAttributeValue
            {
                DataType = "String",
                StringValue = options.UniqueId
            };
        }

        if (options.Properties is not null)
        {
            foreach (var property in options.Properties)
            {
                publishRequest.MessageAttributes[property.Key] = new SnsMessageAttributeValue
                {
                    DataType = "String",
                    StringValue = property.Value
                };
            }
        }

        await _resiliencePolicy.ExecuteAsync(async _ =>
        {
            try
            {
                await _snsClient.Value.PublishAsync(publishRequest, cancellationToken).AnyContext();
            }
            catch (SnsNotFoundException ex)
            {
                throw new MessageBusException($"SNS topic {topicName} not found: {ex.Message}", ex);
            }
            catch (AmazonServiceException ex)
            {
                throw new MessageBusException($"Failed to publish message to SNS topic {topicName}: {ex.Message}", ex);
            }
        }, cancellationToken).AnyContext();
    }

    /// <summary>
    /// Asynchronously releases all resources used by the message bus.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Disposal performs the following cleanup operations:
    /// <list type="bullet">
    /// <item>Cancels and waits for the subscriber polling loop to complete</item>
    /// <item>Unsubscribes the SQS queue from the SNS topic</item>
    /// <item>Deletes the SQS queue if <see cref="SQSMessageBusOptions.SubscriptionQueueAutoDelete"/> is true</item>
    /// <item>Disposes the SNS and SQS clients</item>
    /// </list>
    /// </para>
    /// <para>
    /// For durable subscriptions, set <see cref="SQSMessageBusOptions.SubscriptionQueueAutoDelete"/> to false
    /// to preserve the queue across restarts.
    /// </para>
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        _subscriberCts?.Cancel();
        if (_subscriberTask is not null)
        {
            try
            {
                await _subscriberTask.AnyContext();
            }
            catch (OperationCanceledException)
            {
                // Expected when the subscriber is canceled during shutdown
            }
        }

        _subscriberCts?.Dispose();

        if (!String.IsNullOrEmpty(_subscriptionArn) && _snsClient.IsValueCreated)
        {
            try
            {
                await _snsClient.Value.UnsubscribeAsync(_subscriptionArn).AnyContext();
            }
            catch (SnsNotFoundException)
            {
                // Subscription already deleted
            }
            catch (AmazonServiceException ex)
            {
                _logger.LogDebug(ex, "Error unsubscribing from SNS topic during dispose");
            }
        }

        if (!String.IsNullOrEmpty(_queueUrl) && _sqsClient.IsValueCreated && _options.SubscriptionQueueAutoDelete)
        {
            try
            {
                await _sqsClient.Value.DeleteQueueAsync(_queueUrl).AnyContext();
            }
            catch (QueueDoesNotExistException)
            {
                // Queue already deleted
            }
            catch (AmazonServiceException ex)
            {
                _logger.LogDebug(ex, "Error deleting SQS queue during dispose");
            }
        }

        if (_snsClient.IsValueCreated)
            _snsClient.Value.Dispose();

        if (_sqsClient.IsValueCreated)
            _sqsClient.Value.Dispose();

        base.Dispose();
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Validates that a queue name conforms to SQS naming requirements.
    /// </summary>
    /// <param name="name">The queue name to validate.</param>
    /// <param name="parameterName">The parameter name for exception messages.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when the name is null, empty, longer than 80 characters, or contains invalid characters.
    /// </exception>
    /// <remarks>
    /// SQS queue names must be 1-80 characters and can only contain alphanumeric characters,
    /// hyphens (-), and underscores (_).
    /// </remarks>
    private static void ValidateQueueName(string name, string parameterName)
    {
        ArgumentException.ThrowIfNullOrEmpty(name, parameterName);

        if (name.Length > 80)
            throw new ArgumentException($"Queue name must be 80 characters or less, got {name.Length} characters.", parameterName);

        char invalidChar = name.FirstOrDefault(c => !Char.IsLetterOrDigit(c) && c != '-' && c != '_');
        if (invalidChar != default)
            throw new ArgumentException($"Queue name contains invalid character '{invalidChar}'. Only alphanumeric characters, hyphens, and underscores are allowed.", parameterName);
    }

    /// <summary>
    /// Gets the SQS queue name for this subscription.
    /// </summary>
    /// <returns>
    /// The configured <see cref="SQSMessageBusOptions.SubscriptionQueueName"/> if set,
    /// otherwise a generated name based on the topic name and a unique identifier.
    /// </returns>
    /// <remarks>
    /// Generated queue names follow the pattern "{Topic}-{Guid}" and are truncated to 80 characters
    /// to comply with SQS naming limits. The configured queue name is validated in the constructor.
    /// </remarks>
    private string GetSubscriptionQueueName()
    {
        // SubscriptionQueueName is validated in constructor, no need to validate again
        if (!String.IsNullOrEmpty(_options.SubscriptionQueueName))
            return _options.SubscriptionQueueName;

        string queueName = $"{_options.Topic}-{Guid.NewGuid():N}";
        if (queueName.Length > 80)
            queueName = queueName[..80];

        return queueName;
    }

    /// <summary>
    /// Checks if the existing policy already contains a statement with the specified Sid.
    /// </summary>
    /// <param name="existingPolicy">The existing IAM policy JSON, or null/empty if no policy exists.</param>
    /// <param name="sid">The statement ID to search for.</param>
    /// <returns>True if the policy contains a statement with the specified Sid; otherwise, false.</returns>
    /// <remarks>
    /// This method is used to avoid redundant SetQueueAttributes calls when the policy already
    /// contains the required SNS permission. If the policy cannot be parsed, it returns false
    /// to trigger a policy update.
    /// </remarks>
    private static bool PolicyContainsStatement(string existingPolicy, string sid)
    {
        if (String.IsNullOrEmpty(existingPolicy))
            return false;

        try
        {
            var policy = JsonNode.Parse(existingPolicy)?.AsObject();
            var statements = policy?["Statement"]?.AsArray();
            if (statements is null)
                return false;

            return statements.Any(stmt => stmt?.AsObject()?["Sid"]?.GetValue<string>() == sid);
        }
        catch
        {
            // If we can't parse the policy, assume we need to update it
        }

        return false;
    }

    /// <summary>
    /// Merges a new SNS-to-SQS policy statement into an existing queue policy, or creates a new policy if none exists.
    /// </summary>
    /// <param name="existingPolicy">The existing IAM policy JSON, or null/empty if no policy exists.</param>
    /// <param name="queueArn">The ARN of the SQS queue to grant access to.</param>
    /// <param name="topicArn">The ARN of the SNS topic that will send messages.</param>
    /// <returns>The merged policy JSON string.</returns>
    /// <remarks>
    /// <para>
    /// This method ensures that durable queues can be reused across restarts without losing existing permissions.
    /// Each SNS topic gets a unique statement ID (Sid) based on its ARN hash, formatted as "AllowSNS-{hash}".
    /// </para>
    /// <para>
    /// The merge behavior:
    /// <list type="bullet">
    /// <item>If no policy exists, creates a new policy with the SNS permission</item>
    /// <item>If a statement with the same Sid exists, replaces it (in case the queue ARN changed)</item>
    /// <item>If no matching statement exists, adds the new statement to the existing policy</item>
    /// </list>
    /// </para>
    /// <para>
    /// This approach allows a single SQS queue to receive messages from multiple SNS topics if needed,
    /// though the typical use case is a 1-1 relationship.
    /// </para>
    /// </remarks>
    private static string GetMergedQueuePolicy(string existingPolicy, string queueArn, string topicArn)
    {
        var newStatement = new JsonObject
        {
            ["Sid"] = $"AllowSNS-{topicArn.GetHashCode():X8}",
            ["Effect"] = "Allow",
            ["Principal"] = new JsonObject { ["Service"] = "sns.amazonaws.com" },
            ["Action"] = "sqs:SendMessage",
            ["Resource"] = queueArn,
            ["Condition"] = new JsonObject
            {
                ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = topicArn }
            }
        };

        if (String.IsNullOrEmpty(existingPolicy))
        {
            var newPolicy = new JsonObject
            {
                ["Version"] = "2012-10-17",
                ["Statement"] = new JsonArray { newStatement }
            };
            return newPolicy.ToJsonString();
        }

        var policy = JsonNode.Parse(existingPolicy)?.AsObject();
        if (policy is null)
        {
            var newPolicy = new JsonObject
            {
                ["Version"] = "2012-10-17",
                ["Statement"] = new JsonArray { newStatement }
            };
            return newPolicy.ToJsonString();
        }

        var statements = policy["Statement"]?.AsArray();
        if (statements is null)
        {
            statements = new JsonArray();
            policy["Statement"] = statements;
        }

        // Check if a statement for this topic already exists
        string newSid = newStatement["Sid"]?.GetValue<string>();
        bool found = false;
        for (int i = 0; i < statements.Count; i++)
        {
            var stmt = statements[i]?.AsObject();
            if (stmt is null)
                continue;

            string sid = stmt["Sid"]?.GetValue<string>();
            if (sid == newSid)
            {
                // Replace existing statement for this topic
                statements[i] = newStatement;
                found = true;
                break;
            }
        }

        if (!found)
            statements.Add(newStatement);

        return policy.ToJsonString();
    }
}

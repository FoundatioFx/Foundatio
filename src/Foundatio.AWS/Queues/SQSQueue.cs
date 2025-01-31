using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Foundatio.AsyncEx;
using Foundatio.Extensions;
using Foundatio.Serializer;
using Microsoft.Extensions.Logging;
using ThirdParty.Json.LitJson;

namespace Foundatio.Queues;

public class SQSQueue<T> : QueueBase<T, SQSQueueOptions<T>> where T : class
{
    private readonly AsyncLock _lock = new();
    private readonly Lazy<AmazonSQSClient> _client;
    private string _queueUrl;
    private string _deadUrl;

    private long _enqueuedCount;
    private long _dequeuedCount;
    private long _completedCount;
    private long _abandonedCount;
    private long _workerErrorCount;

    public SQSQueue(SQSQueueOptions<T> options) : base(options)
    {
        // TODO: Flow through the options like retries and the like.
        _client = new Lazy<AmazonSQSClient>(() =>
        {
            var credentials = options.Credentials ?? FallbackCredentialsFactory.GetCredentials();

            if (String.IsNullOrEmpty(options.ServiceUrl))
            {
                var region = options.Region ?? FallbackRegionFactory.GetRegionEndpoint();
                return new AmazonSQSClient(credentials, region);
            }

            return new AmazonSQSClient(
                credentials,
                new AmazonSQSConfig
                {
                    RegionEndpoint = RegionEndpoint.USEast1,
                    ServiceURL = options.ServiceUrl
                });
        });
    }

    public SQSQueue(Builder<SQSQueueOptionsBuilder<T>, SQSQueueOptions<T>> builder)
        : this(builder(new SQSQueueOptionsBuilder<T>()).Build()) { }

    public AmazonSQSClient Client => _client.Value;

    protected override async Task EnsureQueueCreatedAsync(CancellationToken cancellationToken = new CancellationToken())
    {
        if (!String.IsNullOrEmpty(_queueUrl))
            return;

        using (await _lock.LockAsync(cancellationToken).AnyContext())
        {
            if (!String.IsNullOrEmpty(_queueUrl))
                return;

            try
            {
                var urlResponse = await _client.Value.GetQueueUrlAsync(_options.Name, cancellationToken).AnyContext();
                _queueUrl = urlResponse.QueueUrl;
            }
            catch (QueueDoesNotExistException)
            {
                if (!_options.CanCreateQueue)
                    throw;
            }

            if (!String.IsNullOrEmpty(_queueUrl))
                return;

            await CreateQueueAsync().AnyContext();
        }
    }

    protected override async Task<string> EnqueueImplAsync(T data, QueueEntryOptions options)
    {
        if (!await OnEnqueuingAsync(data, options).AnyContext())
            return null;

        int delaySeconds = (int)options.DeliveryDelay.GetValueOrDefault().TotalSeconds;
        if (delaySeconds < 0)
            delaySeconds = 0;

        var message = new SendMessageRequest
        {
            QueueUrl = _queueUrl,
            DelaySeconds = delaySeconds,
            MessageBody = _serializer.SerializeToString(data)
        };

        if (!String.IsNullOrEmpty(options.UniqueId))
            message.MessageDeduplicationId = options.UniqueId;

        if (!String.IsNullOrEmpty(options.CorrelationId))
            message.MessageAttributes.Add("CorrelationId", new MessageAttributeValue
            {
                DataType = "String",
                StringValue = options.CorrelationId
            });

        if (options.Properties is not null)
        {
            foreach (var property in options.Properties)
                message.MessageAttributes.Add(property.Key, new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = property.Value // TODO: Support more than string data types
                });
        }

        var response = await _client.Value.SendMessageAsync(message).AnyContext();
        if (response.HttpStatusCode != System.Net.HttpStatusCode.OK)
            throw new InvalidOperationException("Failed to send SQS message.");

        _logger.LogTrace("Enqueued SQS message {MessageId}", response.MessageId);

        Interlocked.Increment(ref _enqueuedCount);
        var entry = new QueueEntry<T>(response.MessageId, options.CorrelationId, data, this, _timeProvider.GetUtcNow().UtcDateTime, 0);
        await OnEnqueuedAsync(entry).AnyContext();

        return response.MessageId;
    }

    protected override async Task<IQueueEntry<T>> DequeueImplAsync(CancellationToken linkedCancellationToken)
    {
        // sqs doesn't support already canceled token, change timeout and token for sqs pattern
        int visibilityTimeout = (int)Math.Round(_options.WorkItemTimeout.TotalSeconds, MidpointRounding.AwayFromZero);
        int waitTimeout = linkedCancellationToken.IsCancellationRequested ? 0 : (int)Math.Round(_options.ReadQueueTimeout.TotalSeconds, MidpointRounding.AwayFromZero);

        var request = new ReceiveMessageRequest
        {
            QueueUrl = _queueUrl,
            MaxNumberOfMessages = 1,
            VisibilityTimeout = visibilityTimeout,
            WaitTimeSeconds = waitTimeout,
            MessageSystemAttributeNames = ["All"],
            MessageAttributeNames = ["All"]
        };

        // receive message local function
        Task<ReceiveMessageResponse> ReceiveMessageAsync()
        {
            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("Checking for SQS message... IsCancellationRequested={IsCancellationRequested} VisibilityTimeout={VisibilityTimeout} WaitTimeSeconds={WaitTimeSeconds}", linkedCancellationToken.IsCancellationRequested, visibilityTimeout, waitTimeout);

            // The aws sdk will not abort a http long pull operation when the cancellation token is cancelled.
            // The aws sdk will throw the OperationCanceledException after the long poll http call is returned and
            // the message will be marked as in-flight but not returned from this call: https://github.com/aws/aws-sdk-net/issues/1680
            return _client.Value.ReceiveMessageAsync(request, CancellationToken.None);
        }

        var response = await ReceiveMessageAsync().AnyContext();

        // retry loop
        while ((response == null || response.Messages.Count == 0) && !linkedCancellationToken.IsCancellationRequested)
        {
            if (_options.DequeueInterval > TimeSpan.Zero)
            {
                try
                {
                    await _timeProvider.Delay(_options.DequeueInterval, linkedCancellationToken).AnyContext();
                }
                catch (OperationCanceledException)
                {
                    _logger.LogTrace("Operation cancelled while waiting to retry dequeue");
                }
            }

            response = await ReceiveMessageAsync().AnyContext();
        }

        if (response == null || response.Messages.Count == 0)
        {
            _logger.LogTrace("Response null or 0 message count");
            return null;
        }

        Interlocked.Increment(ref _dequeuedCount);

        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Received message {MessageId} IsCancellationRequested={IsCancellationRequested}", response.Messages[0].MessageId, linkedCancellationToken.IsCancellationRequested);

        var message = response.Messages.First();
        string body = message.Body;
        var data = _serializer.Deserialize<T>(body);
        var entry = new SQSQueueEntry<T>(message, data, this);

        await OnDequeuedAsync(entry).AnyContext();

        return entry;
    }

    public override async Task RenewLockAsync(IQueueEntry<T> queueEntry)
    {
        if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("Queue {Name} renew lock item: {EntryId}", _options.Name, queueEntry.Id);

        var entry = ToQueueEntry(queueEntry);
        int visibilityTimeout = (int)Math.Round(_options.WorkItemTimeout.TotalSeconds, MidpointRounding.AwayFromZero);
        var request = new ChangeMessageVisibilityRequest
        {
            QueueUrl = _queueUrl,
            VisibilityTimeout = visibilityTimeout,
            ReceiptHandle = entry.UnderlyingMessage.ReceiptHandle
        };

        await _client.Value.ChangeMessageVisibilityAsync(request).AnyContext();
        await OnLockRenewedAsync(entry).AnyContext();

        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Renew lock done: {EntryId} MessageId={MessageId} VisibilityTimeout={VisibilityTimeout}", queueEntry.Id, entry.UnderlyingMessage.MessageId, visibilityTimeout);
    }

    public override async Task CompleteAsync(IQueueEntry<T> queueEntry)
    {
        if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("Queue {Name} complete item: {EntryId}", _options.Name, queueEntry.Id);
        if (queueEntry.IsAbandoned || queueEntry.IsCompleted)
            throw new InvalidOperationException("Queue entry has already been completed or abandoned.");

        var entry = ToQueueEntry(queueEntry);
        var request = new DeleteMessageRequest
        {
            QueueUrl = _queueUrl,
            ReceiptHandle = entry.UnderlyingMessage.ReceiptHandle,
        };

        await _client.Value.DeleteMessageAsync(request).AnyContext();

        Interlocked.Increment(ref _completedCount);
        queueEntry.MarkCompleted();
        await OnCompletedAsync(queueEntry).AnyContext();
        if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Complete done: {EntryId}", queueEntry.Id);
    }

    public override async Task AbandonAsync(IQueueEntry<T> entry)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Queue {Name}:{QueueId} abandon item: {EntryId}", _options.Name, QueueId, entry.Id);

        if (entry.IsAbandoned || entry.IsCompleted)
            throw new InvalidOperationException("Queue entry has already been completed or abandoned.");

        var sqsQueueEntry = ToQueueEntry(entry);
        int visibilityTimeout = (int)Math.Round(_options.RetryDelay(sqsQueueEntry.Attempts).TotalSeconds, MidpointRounding.AwayFromZero);

        // re-queue and wait for processing
        var request = new ChangeMessageVisibilityRequest
        {
            QueueUrl = _queueUrl,
            VisibilityTimeout = visibilityTimeout,
            ReceiptHandle = sqsQueueEntry.UnderlyingMessage.ReceiptHandle,
        };

        await _client.Value.ChangeMessageVisibilityAsync(request).AnyContext();
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Abandoned queue entry: {EntryId} MessageId={MessageId} VisibilityTimeout={VisibilityTimeout}", sqsQueueEntry.Id, sqsQueueEntry.UnderlyingMessage.MessageId, visibilityTimeout);

        Interlocked.Increment(ref _abandonedCount);
        entry.MarkAbandoned();

        await OnAbandonedAsync(sqsQueueEntry).AnyContext();
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Abandon complete: {EntryId}", entry.Id);
    }

    protected override Task<IEnumerable<T>> GetDeadletterItemsImplAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    protected override async Task<QueueStats> GetQueueStatsImplAsync()
    {
        if (String.IsNullOrEmpty(_queueUrl))
            return new QueueStats
            {
                Queued = 0,
                Working = 0,
                Deadletter = 0,
                Enqueued = _enqueuedCount,
                Dequeued = _dequeuedCount,
                Completed = _completedCount,
                Abandoned = _abandonedCount,
                Errors = _workerErrorCount,
                Timeouts = 0
            };

        var attributeNames = new List<string> { QueueAttributeName.All };
        var queueRequest = new GetQueueAttributesRequest(_queueUrl, attributeNames);
        var queueAttributes = await _client.Value.GetQueueAttributesAsync(queueRequest).AnyContext();

        int queueCount = queueAttributes.ApproximateNumberOfMessages;
        int workingCount = queueAttributes.ApproximateNumberOfMessagesNotVisible;
        int deadCount = 0;

        // dead letter supported
        if (!_options.SupportDeadLetter)
        {
            return new QueueStats
            {
                Queued = queueCount,
                Working = workingCount,
                Deadletter = deadCount,
                Enqueued = _enqueuedCount,
                Dequeued = _dequeuedCount,
                Completed = _completedCount,
                Abandoned = _abandonedCount,
                Errors = _workerErrorCount,
                Timeouts = 0
            };
        }

        // lookup dead letter url
        if (String.IsNullOrEmpty(_deadUrl))
        {
            string deadLetterName = queueAttributes.Attributes.DeadLetterQueue();
            if (!String.IsNullOrEmpty(deadLetterName))
            {
                var deadResponse = await _client.Value.GetQueueUrlAsync(deadLetterName).AnyContext();
                _deadUrl = deadResponse.QueueUrl;
            }
        }

        // get attributes from dead letter
        if (!String.IsNullOrEmpty(_deadUrl))
        {
            var deadRequest = new GetQueueAttributesRequest(_deadUrl, attributeNames);
            var deadAttributes = await _client.Value.GetQueueAttributesAsync(deadRequest).AnyContext();
            deadCount = deadAttributes.ApproximateNumberOfMessages;
        }

        return new QueueStats
        {
            Queued = queueCount,
            Working = workingCount,
            Deadletter = deadCount,
            Enqueued = _enqueuedCount,
            Dequeued = _dequeuedCount,
            Completed = _completedCount,
            Abandoned = _abandonedCount,
            Errors = _workerErrorCount,
            Timeouts = 0
        };
    }

    public override async Task DeleteQueueAsync()
    {
        if (!String.IsNullOrEmpty(_queueUrl))
        {
            await _client.Value.DeleteQueueAsync(_queueUrl).AnyContext();
        }
        if (!String.IsNullOrEmpty(_deadUrl))
        {
            await _client.Value.DeleteQueueAsync(_deadUrl).AnyContext();
        }

        _enqueuedCount = 0;
        _dequeuedCount = 0;
        _completedCount = 0;
        _abandonedCount = 0;
        _workerErrorCount = 0;
    }

    protected override void StartWorkingImpl(Func<IQueueEntry<T>, CancellationToken, Task> handler, bool autoComplete, CancellationToken cancellationToken)
    {
        if (handler == null)
            throw new ArgumentNullException(nameof(handler));

        var linkedCancellationToken = GetLinkedDisposableCancellationTokenSource(cancellationToken);

        Task.Run(async () =>
        {
            bool isTraceLevelLogging = _logger.IsEnabled(LogLevel.Trace);
            if (isTraceLevelLogging) _logger.LogTrace("WorkerLoop Start {Name}", _options.Name);

            while (!linkedCancellationToken.IsCancellationRequested)
            {
                if (isTraceLevelLogging) _logger.LogTrace("WorkerLoop Signaled {Name}", _options.Name);

                IQueueEntry<T> entry = null;
                try
                {
                    entry = await DequeueImplAsync(linkedCancellationToken.Token).AnyContext();
                }
                catch (OperationCanceledException) { }

                if (linkedCancellationToken.IsCancellationRequested || entry == null)
                    continue;

                try
                {
                    await handler(entry, linkedCancellationToken.Token).AnyContext();
                    if (autoComplete && !entry.IsAbandoned && !entry.IsCompleted && !linkedCancellationToken.IsCancellationRequested)
                        await entry.CompleteAsync().AnyContext();
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _workerErrorCount);
                    if (_logger.IsEnabled(LogLevel.Error)) _logger.LogError(ex, "Worker error: {Message}", ex.Message);

                    if (!entry.IsAbandoned && !entry.IsCompleted && !linkedCancellationToken.IsCancellationRequested)
                        await entry.AbandonAsync().AnyContext();
                }
            }

            if (isTraceLevelLogging) _logger.LogTrace("Worker exiting: {Name} IsCancellationRequested={IsCancellationRequested}", _options.Name, linkedCancellationToken.IsCancellationRequested);
        }, linkedCancellationToken.Token).ContinueWith(_ => linkedCancellationToken.Dispose());
    }

    public override void Dispose()
    {
        base.Dispose();

        if (_client.IsValueCreated)
            _client.Value.Dispose();
    }

    protected virtual async Task CreateQueueAsync()
    {
        // step 1, create queue
        var createQueueRequest = new CreateQueueRequest
        {
            QueueName = _options.Name,
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

        var createQueueResponse = await _client.Value.CreateQueueAsync(createQueueRequest).AnyContext();
        _queueUrl = createQueueResponse.QueueUrl;

        if (!_options.SupportDeadLetter)
            return;

        // step 2, create dead letter queue
        var createDeadRequest = new CreateQueueRequest { QueueName = _options.Name + "-deadletter" };
        var createDeadResponse = await _client.Value.CreateQueueAsync(createDeadRequest).AnyContext();
        _deadUrl = createDeadResponse.QueueUrl;

        // step 3, get dead letter attributes
        var attributeNames = new List<string> { QueueAttributeName.QueueArn };
        var deadAttributeRequest = new GetQueueAttributesRequest(_deadUrl, attributeNames);
        var deadAttributeResponse = await _client.Value.GetQueueAttributesAsync(deadAttributeRequest).AnyContext();

        var maxReceiveCount = Math.Max(_options.Retries + 1, 1);
        // step 4, set retry policy
        var redrivePolicy = new JsonData
        {
            ["maxReceiveCount"] = maxReceiveCount.ToString(),
            ["deadLetterTargetArn"] = deadAttributeResponse.QueueARN
        };

        var attributes = new Dictionary<string, string>
        {
            [QueueAttributeName.RedrivePolicy] = JsonMapper.ToJson(redrivePolicy)
        };

        var setAttributeRequest = new SetQueueAttributesRequest(_queueUrl, attributes);
        await _client.Value.SetQueueAttributesAsync(setAttributeRequest).AnyContext();
    }

    private static SQSQueueEntry<T> ToQueueEntry(IQueueEntry<T> entry)
    {
        if (entry is not SQSQueueEntry<T> result)
            throw new ArgumentException($"Expected {nameof(SQSQueueEntry<T>)} but received unknown queue entry type {entry.GetType()}");

        return result;
    }
}

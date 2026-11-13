using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.Credentials;
using Amazon.SQS;
using Amazon.SQS.Model;
using Foundatio.AsyncEx;
using Foundatio.Extensions;
using Foundatio.Serializer;
using Microsoft.Extensions.Logging;

namespace Foundatio.Queues;

public class SQSQueue<T> : QueueBase<T, SQSQueueOptions<T>> where T : class
{
    /// <summary>
    /// SQS allows at most 10 message attributes per message.
    /// See <see href="https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-message-metadata.html">SQS message metadata</see>.
    /// </summary>
    private const int MaxMessageAttributes = 10;

    /// <summary>
    /// Maximum length of <c>MessageGroupId</c> and <c>MessageDeduplicationId</c>.
    /// See <see href="https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_SendMessage.html">SendMessage</see>.
    /// </summary>
    private const int MaxSqsIdLength = 128;

    /// <summary>
    /// Maximum length of a queue name, including the <c>.fifo</c> suffix.
    /// See <see href="https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_CreateQueue.html">CreateQueue</see>.
    /// </summary>
    private const int MaxQueueNameLength = 80;

    /// <summary>
    /// Required suffix of FIFO queue names.
    /// See <see href="https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-fifo-queues.html">FIFO queues</see>.
    /// </summary>
    private const string FifoQueueSuffix = ".fifo";

    /// <summary>
    /// Maximum visibility timeout, which bounds the retry delay of an abandoned message.
    /// See <see href="https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_ChangeMessageVisibility.html">ChangeMessageVisibility</see>.
    /// </summary>
    private const int MaxVisibilityTimeoutSeconds = 43200;


    /// <summary>
    /// Minimum wait after a failed receive in the worker loop. The SDK already retries transient errors with backoff
    /// (see <see href="https://docs.aws.amazon.com/sdkref/latest/guide/feature-retry-behavior.html">retry behavior</see>),
    /// so errors that reach the worker are usually persistent (for example access denied). This keeps them from becoming
    /// a tight request loop when <c>DequeueInterval</c> is zero.
    /// </summary>
    private static readonly TimeSpan MinDequeueErrorBackoff = TimeSpan.FromSeconds(1);

    /// <summary>
    /// SQS attribute changes such as a new redrive policy can take up to 60 seconds to propagate.
    /// See <see href="https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_SetQueueAttributes.html">SetQueueAttributes</see>.
    /// </summary>
    private static readonly TimeSpan QueueAttributePropagationWindow = TimeSpan.FromSeconds(60);


    private readonly AsyncLock _lock = new();
    private readonly Lazy<AmazonSQSClient> _client;
    private readonly bool _isFifo;
    private string? _queueUrl;
    private DeadLetterQueue? _deadLetterQueue;
    /// <summary>
    /// The dead letter queue this instance created (or adopted while creating the source queue). Only this one is deleted
    /// without checking the source's current redrive policy; a queue merely resolved from the policy is revalidated on delete.
    /// </summary>
    private string? _createdDeadLetterUrl;
    private bool _queueSetupPending;
    private bool _contentBasedDeduplication;
    private bool _contentBasedDeduplicationResolved;
    private DateTimeOffset? _deadLetterCreatedUtc;

    private long _enqueuedCount;
    private long _dequeuedCount;
    private long _completedCount;
    private long _abandonedCount;
    private long _workerErrorCount;

    public SQSQueue(SQSQueueOptions<T> options) : base(options)
    {
        _isFifo = _options.Name.EndsWith(FifoQueueSuffix, StringComparison.Ordinal);

        // TODO: Flow through the options like retries and the like.
        _client = new Lazy<AmazonSQSClient>(() =>
        {
            var credentials = options.Credentials ?? DefaultAWSCredentialsIdentityResolver.GetCredentials();

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

    protected override bool SupportsGroupId => true;

    protected override async Task EnsureQueueCreatedAsync(CancellationToken cancellationToken = default)
    {
        if (!String.IsNullOrEmpty(_queueUrl))
            return;

        using (await _lock.LockAsync(cancellationToken).AnyContext())
        {
            if (!String.IsNullOrEmpty(_queueUrl))
                return;

            // A previous attempt created the source queue but failed before its redrive policy was set; finish that setup
            // instead of using the existing queue as-is.
            if (_queueSetupPending)
            {
                await CreateQueueAsync().AnyContext();
                return;
            }

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

    protected override async Task<string?> EnqueueImplAsync(T data, QueueEntryOptions options)
    {
        ValidateEntryOptions(options);

        if (!await OnEnqueuingAsync(data, options).AnyContext())
            return null;

        // Enqueuing handlers may change the options, so the options that are actually sent are validated again.
        ValidateEntryOptions(options);

        var message = new SendMessageRequest
        {
            QueueUrl = _queueUrl,
            MessageBody = _serializer.SerializeToString(data),
            MessageAttributes = CreateMessageAttributes(options)
        };

        bool usesContentBasedDeduplication = _isFifo && await SetFifoDeduplicationIdAsync(message, options).AnyContext();

        if (!String.IsNullOrEmpty(options.GroupId))
            message.MessageGroupId = options.GroupId;

        // NOTE: Any delay defined here will override any delay configured in the SQS.
        if (options.DeliveryDelay.HasValue && !_isFifo)
        {
            int delaySeconds = (int)options.DeliveryDelay.Value.TotalSeconds;
            message.DelaySeconds = Math.Max(0, Math.Min(900, delaySeconds));
        }


        SendMessageResponse response;
        try
        {
            response = await _client.Value.SendMessageAsync(message).AnyContext();
        }
        catch (QueueDoesNotExistException)
        {
            ForgetQueueUrl(message.QueueUrl);
            throw;
        }
        catch (AmazonSQSException ex) when (usesContentBasedDeduplication && IsMissingDeduplicationIdError(ex))
        {
            // Content-based deduplication was turned off since it was read; send once more with a generated id.
            _logger.LogDebug("Queue {QueueName} ContentBasedDeduplication was turned off; resending with a generated deduplication id", _options.Name);
            _contentBasedDeduplication = false;
            message.MessageDeduplicationId = Guid.NewGuid().ToString("N");
            try
            {
                response = await _client.Value.SendMessageAsync(message).AnyContext();
            }
            catch (QueueDoesNotExistException)
            {
                ForgetQueueUrl(message.QueueUrl);
                throw;
            }
        }

        if (response.HttpStatusCode is not System.Net.HttpStatusCode.OK)
            throw new QueueException("Failed to send SQS message.");

        _logger.LogTrace("Enqueued SQS message {MessageId} HasGroupId={HasGroupId}", response.MessageId, !String.IsNullOrEmpty(options.GroupId));

        Interlocked.Increment(ref _enqueuedCount);
        var entry = new QueueEntry<T>(response.MessageId, options.CorrelationId, data, this, _timeProvider.GetUtcNow().UtcDateTime, 0) { GroupId = options.GroupId };
        await OnEnqueuedAsync(entry).AnyContext();

        return response.MessageId;
    }

    protected override async Task<IQueueEntry<T>?> DequeueImplAsync(CancellationToken linkedCancellationToken)
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
            _logger.LogTrace("Checking for SQS message... IsCancellationRequested={IsCancellationRequested} VisibilityTimeout={VisibilityTimeout} WaitTimeSeconds={WaitTimeSeconds}", linkedCancellationToken.IsCancellationRequested, visibilityTimeout, waitTimeout);

            // The aws sdk will not abort a http long pull operation when the cancellation token is cancelled.
            // The aws sdk will throw the OperationCanceledException after the long poll http call is returned and
            // the message will be marked as in-flight but not returned from this call: https://github.com/aws/aws-sdk-net/issues/1680
            return ReceiveAsync();
        }

        async Task<ReceiveMessageResponse> ReceiveAsync()
        {
            try
            {
                return await _client.Value.ReceiveMessageAsync(request, CancellationToken.None).AnyContext();
            }
            catch (QueueDoesNotExistException)
            {
                ForgetQueueUrl(request.QueueUrl);
                throw;
            }
        }

        var response = await ReceiveMessageAsync().AnyContext();

        // retry loop
        while ((response?.Messages is null || response.Messages.Count is 0) && !linkedCancellationToken.IsCancellationRequested)
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

        if (response?.Messages is null || response.Messages.Count is 0)
        {
            _logger.LogTrace("Response null or 0 message count");
            return null;
        }

        Interlocked.Increment(ref _dequeuedCount);

        _logger.LogTrace("Received message {MessageId} IsCancellationRequested={IsCancellationRequested}", response.Messages[0].MessageId, linkedCancellationToken.IsCancellationRequested);

        var message = response.Messages[0];
        string body = message.Body;

        T? data;
        Exception? deserializeException = null;
        try
        {
            data = _serializer.Deserialize<T>(body);
        }
        catch (Exception ex)
        {
            data = null;
            deserializeException = ex;
        }

        if (data is null)
        {
            _logger.LogWarning(deserializeException, "Error deserializing message {MessageId} (receive count {ReceiveCount}), abandoning for retry",
                message.MessageId, message.ApproximateReceiveCount());

            // Poison message: null! is intentional — deserialization failed, entry is immediately abandoned.
            var poisonEntry = new SQSQueueEntry<T>(message, null!, this);
            await AbandonAsync(poisonEntry).AnyContext();
            return null;
        }

        var entry = new SQSQueueEntry<T>(message, data, this);

        if (entry.Attempts > _options.Retries + 1)
        {
            await DeadLetterMessageAsync(entry).AnyContext();
            Interlocked.Increment(ref _abandonedCount);
            return null;
        }

        await OnDequeuedAsync(entry).AnyContext();

        return entry;
    }

    public override async Task RenewLockAsync(IQueueEntry<T> queueEntry)
    {
        _logger.LogDebug("Queue {QueueName} renew lock item: {QueueEntryId}", _options.Name, queueEntry.Id);

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

        _logger.LogTrace("Renew lock done: {QueueEntryId} MessageId={MessageId} VisibilityTimeout={VisibilityTimeout}", queueEntry.Id, entry.UnderlyingMessage.MessageId, visibilityTimeout);
    }

    public override async Task CompleteAsync(IQueueEntry<T> queueEntry)
    {
        _logger.LogDebug("Queue {QueueName} complete item: {QueueEntryId}", _options.Name, queueEntry.Id);
        if (queueEntry.IsAbandoned || queueEntry.IsCompleted)
            throw new QueueException("Queue entry has already been completed or abandoned.");

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
        _logger.LogTrace("Complete done: {QueueEntryId}", queueEntry.Id);
    }

    public override async Task AbandonAsync(IQueueEntry<T> entry)
    {
        _logger.LogDebug("Queue {QueueName} ({QueueId}) abandon item: {QueueEntryId}", _options.Name, QueueId, entry.Id);

        if (entry.IsAbandoned || entry.IsCompleted)
            throw new QueueException("Queue entry has already been completed or abandoned.");

        var sqsQueueEntry = ToQueueEntry(entry);

        if (sqsQueueEntry.Attempts > _options.Retries)
        {
            await DeadLetterMessageAsync(sqsQueueEntry).AnyContext();
        }
        else
        {
            // On FIFO queues the abandoned message keeps its group blocked until it becomes visible again, so the
            // retry delay also delays every later message in the same group.
            double retryDelaySeconds = Math.Round(_options.RetryDelay(sqsQueueEntry.Attempts).TotalSeconds, MidpointRounding.AwayFromZero);
            int visibilityTimeout = (int)Math.Clamp(retryDelaySeconds, 0, MaxVisibilityTimeoutSeconds);

            var request = new ChangeMessageVisibilityRequest
            {
                QueueUrl = _queueUrl,
                VisibilityTimeout = visibilityTimeout,
                ReceiptHandle = sqsQueueEntry.UnderlyingMessage.ReceiptHandle,
            };

            await _client.Value.ChangeMessageVisibilityAsync(request).AnyContext();
            _logger.LogTrace("Abandoned queue entry: {QueueEntryId} MessageId={MessageId} VisibilityTimeout={VisibilityTimeout}", sqsQueueEntry.Id, sqsQueueEntry.UnderlyingMessage.MessageId, visibilityTimeout);
        }

        Interlocked.Increment(ref _abandonedCount);
        entry.MarkAbandoned();

        await OnAbandonedAsync(sqsQueueEntry).AnyContext();
        _logger.LogTrace("Abandon complete: {QueueEntryId}", entry.Id);
    }

    protected override Task<IEnumerable<T>> GetDeadletterItemsImplAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    protected override async Task<QueueStats> GetQueueStatsImplAsync()
    {
        if (String.IsNullOrEmpty(_queueUrl))
            return CreateQueueStats(0, 0);

        var attributeNames = new List<string> { QueueAttributeName.All };
        var queueRequest = new GetQueueAttributesRequest(_queueUrl, attributeNames);
        // Captured before the read so a read that started inside the propagation window keeps the fallback.
        string? propagationFallbackUrl = GetPropagationFallbackUrl();
        GetQueueAttributesResponse queueAttributes;
        try
        {
            queueAttributes = await _client.Value.GetQueueAttributesAsync(queueRequest).AnyContext();
        }
        catch (QueueDoesNotExistException)
        {
            ForgetQueueUrl(queueRequest.QueueUrl);
            throw;
        }

        int queueCount = queueAttributes.ApproximateNumberOfMessages;
        int workingCount = queueAttributes.ApproximateNumberOfMessagesNotVisible;
        if (!_options.SupportDeadLetter)
            return CreateQueueStats(queueCount, workingCount);

        string? deadLetterUrl = await ResolveDeadLetterUrlAsync(queueAttributes.Attributes, propagationFallbackUrl).AnyContext();
        if (deadLetterUrl is null)
            return CreateQueueStats(queueCount, workingCount);

        var deadRequest = new GetQueueAttributesRequest(deadLetterUrl, attributeNames);
        var deadAttributes = await _client.Value.GetQueueAttributesAsync(deadRequest).AnyContext();
        return CreateQueueStats(queueCount, workingCount, deadAttributes.ApproximateNumberOfMessages);
    }

    protected override async Task DeleteQueueImplAsync()
    {
        // Serialized with EnsureQueueCreatedAsync so a concurrent create can't set the queue url (or finish a pending setup)
        // after the queue is deleted, which would leave this instance using a deleted queue.
        using var _ = await _lock.LockAsync().AnyContext();

        // An instance that was never used doesn't know the queue url yet; resolve it so the source queue is deleted together
        // with its dead letter queue rather than left with a redrive policy pointing at a deleted queue. The dead letter queue
        // is resolved before the source is deleted, because its ownership is confirmed from the source's redrive policy.
        string? queueUrl = _queueUrl ?? await FindQueueUrlAsync(_options.Name).AnyContext();
        string? ownedDeadLetterUrl = _createdDeadLetterUrl;
        if (ownedDeadLetterUrl is null && queueUrl is not null && _options.SupportDeadLetter)
        {
            // Kept so a retry after a failed dead letter delete still deletes it once the source and its policy are gone.
            ownedDeadLetterUrl = await FindPolicyDeadLetterUrlAsync(queueUrl).AnyContext();
            _createdDeadLetterUrl = ownedDeadLetterUrl;
        }

        if (queueUrl is not null)
            await DeleteQueueIfExistsAsync(queueUrl, _options.Name).AnyContext();
        else
            _logger.LogDebug("Queue {QueueName} does not exist; nothing to delete", _options.Name);

        if (ownedDeadLetterUrl is not null)
            await DeleteQueueIfExistsAsync(ownedDeadLetterUrl, GetDeadLetterQueueName()).AnyContext();

        _queueUrl = null;
        _queueSetupPending = false;
        _contentBasedDeduplication = false;
        _contentBasedDeduplicationResolved = false;
        _deadLetterQueue = null;
        _createdDeadLetterUrl = null;
        _deadLetterCreatedUtc = null;

        _enqueuedCount = 0;
        _dequeuedCount = 0;
        _completedCount = 0;
        _abandonedCount = 0;
        _workerErrorCount = 0;
    }

    protected override void StartWorkingImpl(Func<IQueueEntry<T>, CancellationToken, Task> handler, bool autoComplete, CancellationToken cancellationToken)
    {
        if (handler is null)
            throw new ArgumentNullException(nameof(handler));

        var linkedCancellationTokenSource = GetLinkedDisposableCancellationTokenSource(cancellationToken);

        Task.Run(async () =>
        {
            _logger.LogTrace("WorkerLoop Start {QueueName}", _options.Name);

            while (!linkedCancellationTokenSource.IsCancellationRequested)
            {
                _logger.LogTrace("WorkerLoop Signaled {QueueName}", _options.Name);

                IQueueEntry<T>? entry = null;
                try
                {
                    // The queue url is cleared when the queue is deleted, so a running worker recreates it before receiving.
                    await EnsureQueueCreatedAsync(linkedCancellationTokenSource.Token).AnyContext();
                    entry = await DequeueImplAsync(linkedCancellationTokenSource.Token).AnyContext();
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _workerErrorCount);
                    _logger.LogError(ex, "Error on Dequeue: {Message}", ex.Message);
                    var errorBackoff = _options.DequeueInterval > MinDequeueErrorBackoff ? _options.DequeueInterval : MinDequeueErrorBackoff;
                    try
                    {
                        await _timeProvider.Delay(errorBackoff, linkedCancellationTokenSource.Token).AnyContext();
                    }
                    catch (OperationCanceledException) { }
                }

                if (linkedCancellationTokenSource.IsCancellationRequested || entry is null)
                    continue;

                try
                {
                    await handler(entry, linkedCancellationTokenSource.Token).AnyContext();
                    if (autoComplete && !entry.IsAbandoned && !entry.IsCompleted && !linkedCancellationTokenSource.IsCancellationRequested)
                        await entry.CompleteAsync().AnyContext();
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _workerErrorCount);
                    _logger.LogError(ex, "Worker error: {Message}", ex.Message);

                    if (!entry.IsAbandoned && !entry.IsCompleted && !linkedCancellationTokenSource.IsCancellationRequested)
                    {
                        try
                        {
                            await entry.AbandonAsync().AnyContext();
                        }
                        catch (Exception abandonEx)
                        {
                            _logger.LogError(abandonEx, "Worker error abandoning queue entry {QueueEntryId}; it will be redelivered after its visibility timeout: {Message}", entry.Id, abandonEx.Message);
                        }
                    }
                }
            }

            _logger.LogTrace("Worker exiting: {QueueName} IsCancellationRequested={IsCancellationRequested}", _options.Name, linkedCancellationTokenSource.IsCancellationRequested);
        }, linkedCancellationTokenSource.Token).ContinueWith(_ => linkedCancellationTokenSource.Dispose());
    }

    public override void Dispose()
    {
        if (!SignalDispose())
        {
            _logger.LogTrace("Queue {QueueName} ({QueueId}) dispose was already called", _options.Name, QueueId);
            return;
        }

        if (_client.IsValueCreated)
            _client.Value.Dispose();

        base.Dispose();
    }

    protected virtual async Task CreateQueueAsync()
    {
        string deadLetterName = GetDeadLetterQueueName();
        if (_options.SupportDeadLetter && deadLetterName.Length > MaxQueueNameLength)
            throw new QueueException($"The dead letter queue name {deadLetterName} exceeds the SQS limit of {MaxQueueNameLength} characters; use a queue name of at most {MaxQueueNameLength - (deadLetterName.Length - _options.Name.Length)} characters.");

        // step 1, create queue. _queueUrl is only set once the whole setup succeeds, so a partially created queue is
        // never used without its redrive policy (exhausted messages would otherwise be deleted instead of dead lettered).
        // Setup is marked pending before the call, because SQS can create the queue even when the response is lost; the
        // retry then calls CreateQueue again, which returns the existing queue when its attributes match.
        var createQueueRequest = new CreateQueueRequest
        {
            QueueName = _options.Name,
            Attributes = GetQueueTypeAttributes()
        };

        _queueSetupPending = _options.SupportDeadLetter;
        string queueUrl;
        try
        {
            var createQueueResponse = await _client.Value.CreateQueueAsync(createQueueRequest).AnyContext();
            queueUrl = createQueueResponse.QueueUrl;
        }
        catch (QueueNameExistsException)
        {
            // Another party created the queue with different attributes; the next call uses it as an existing queue
            // instead of retrying a CreateQueue that can't succeed.
            _queueSetupPending = false;
            throw;
        }

        if (!_options.SupportDeadLetter)
        {
            _queueUrl = queueUrl;
            return;
        }

        // step 2, create dead letter queue with the same encryption as the source queue
        var createDeadRequest = new CreateQueueRequest
        {
            QueueName = deadLetterName,
            Attributes = GetQueueTypeAttributes()
        };
        string deadLetterUrl;
        try
        {
            var createDeadResponse = await _client.Value.CreateQueueAsync(createDeadRequest).AnyContext();
            deadLetterUrl = createDeadResponse.QueueUrl;
        }
        catch (QueueNameExistsException ex)
        {
            // A dead letter queue left by an earlier version can have different attributes (for example no encryption);
            // keep using it rather than failing queue creation.
            _logger.LogWarning(ex, "Dead letter queue {DeadLetterQueueName} already exists with different attributes; using it as-is", deadLetterName);
            deadLetterUrl = (await _client.Value.GetQueueUrlAsync(deadLetterName).AnyContext()).QueueUrl;
        }

        _deadLetterQueue = new DeadLetterQueue(deadLetterName, deadLetterUrl);
        _createdDeadLetterUrl = deadLetterUrl;

        // step 3, get dead letter attributes
        var attributeNames = new List<string> { QueueAttributeName.QueueArn };
        var deadAttributeRequest = new GetQueueAttributesRequest(deadLetterUrl, attributeNames);
        var deadAttributeResponse = await _client.Value.GetQueueAttributesAsync(deadAttributeRequest).AnyContext();

        int maxReceiveCount = Math.Max(_options.Retries + 1, 1);
        // step 4, set retry policy
        var redrivePolicy = new JsonObject
        {
            ["maxReceiveCount"] = maxReceiveCount.ToString(),
            ["deadLetterTargetArn"] = deadAttributeResponse.QueueARN
        };

        var attributes = new Dictionary<string, string>
        {
            [QueueAttributeName.RedrivePolicy] = redrivePolicy.ToJsonString()
        };

        var setAttributeRequest = new SetQueueAttributesRequest(queueUrl, attributes);
        await _client.Value.SetQueueAttributesAsync(setAttributeRequest).AnyContext();
        _deadLetterCreatedUtc = _timeProvider.GetUtcNow();
        _queueSetupPending = false;
        _queueUrl = queueUrl;
    }

    /// <summary>
    /// A queue this instance just created may not report its redrive policy yet, so during that window an empty policy
    /// falls back to the dead letter queue it created instead of deleting messages; afterwards a removed policy is honored.
    /// Callers capture this before reading the policy so a read that started inside the window keeps the fallback.
    /// </summary>
    private string? GetPropagationFallbackUrl()
    {
        return _deadLetterCreatedUtc is { } createdUtc && _timeProvider.GetUtcNow() - createdUtc < QueueAttributePropagationWindow
            ? _createdDeadLetterUrl
            : null;
    }

    private async Task<string?> FindQueueUrlAsync(string queueName)
    {
        try
        {
            var response = await _client.Value.GetQueueUrlAsync(queueName).AnyContext();
            return response.QueueUrl;
        }
        catch (QueueDoesNotExistException)
        {
            return null;
        }
    }

    private Dictionary<string, string> GetQueueTypeAttributes()
    {
        var attributes = new Dictionary<string, string>();
        if (_isFifo)
            attributes[QueueAttributeName.FifoQueue] = "true";

        if (_options.SqsManagedSseEnabled)
        {
            attributes[QueueAttributeName.SqsManagedSseEnabled] = "true";
        }
        else if (!String.IsNullOrEmpty(_options.KmsMasterKeyId))
        {
            attributes[QueueAttributeName.KmsMasterKeyId] = _options.KmsMasterKeyId;
            attributes[QueueAttributeName.KmsDataKeyReusePeriodSeconds] = _options.KmsDataKeyReusePeriodSeconds.ToString();
        }

        return attributes;
    }

    /// <summary>
    /// SQS requires FIFO queue names (including a FIFO queue's dead letter queue) to end with <c>.fifo</c>.
    /// </summary>
    private string GetDeadLetterQueueName()
    {
        return _isFifo
            ? _options.Name[..^FifoQueueSuffix.Length] + "-deadletter" + FifoQueueSuffix
            : _options.Name + "-deadletter";
    }

    /// <summary>
    /// Resolves the dead letter queue from the source queue's current redrive policy, so a policy that is added, changed or
    /// removed while this instance is running is honored. The URL is cached per dead letter queue name, so an unchanged
    /// policy costs no <c>GetQueueUrl</c> call. Lookup failures propagate so exhausted messages are retained.
    /// Resolving a queue here doesn't make it owned: <see cref="QueueBase{T,TOptions}.DeleteQueueAsync"/> re-reads the policy before deleting one it didn't create.
    /// </summary>
    private async Task<string?> ResolveDeadLetterUrlAsync()
    {
        var attributeNames = new List<string> { QueueAttributeName.RedrivePolicy };
        var request = new GetQueueAttributesRequest(_queueUrl, attributeNames);
        // Captured before the read so a read that started inside the propagation window keeps the fallback.
        string? propagationFallbackUrl = GetPropagationFallbackUrl();
        GetQueueAttributesResponse response;
        try
        {
            response = await _client.Value.GetQueueAttributesAsync(request).AnyContext();
        }
        catch (QueueDoesNotExistException)
        {
            ForgetQueueUrl(request.QueueUrl);
            throw;
        }
        catch (AmazonServiceException ex)
        {
            _logger.LogWarning(ex, "Unable to read the redrive policy for {QueueName}: {Message}", _options.Name, ex.Message);
            throw;
        }

        return await ResolveDeadLetterUrlAsync(response.Attributes, propagationFallbackUrl).AnyContext();
    }

    /// <summary>
    /// Resolves the dead letter queue from source queue attributes the caller already read.
    /// </summary>
    /// <param name="queueAttributes">Source queue attributes the caller already read.</param>
    /// <param name="propagationFallbackUrl">The <see cref="GetPropagationFallbackUrl"/> result captured before
    /// <paramref name="queueAttributes"/> were read.</param>
    private async Task<string?> ResolveDeadLetterUrlAsync(IDictionary<string, string> queueAttributes, string? propagationFallbackUrl)
    {
        string? deadLetterName = null;
        try
        {
            deadLetterName = queueAttributes.DeadLetterQueue();
            if (String.IsNullOrEmpty(deadLetterName))
                return propagationFallbackUrl;

            var cached = _deadLetterQueue;
            if (cached is not null && String.Equals(cached.Name, deadLetterName, StringComparison.Ordinal))
                return cached.Url;

            var deadResponse = await _client.Value.GetQueueUrlAsync(deadLetterName).AnyContext();
            if (String.IsNullOrEmpty(deadResponse.QueueUrl))
                throw new QueueException($"Unable to resolve dead letter queue {deadLetterName} for {_options.Name}.");

            _deadLetterQueue = new DeadLetterQueue(deadLetterName, deadResponse.QueueUrl);
            return deadResponse.QueueUrl;
        }
        catch (Exception ex) when (ex is AmazonServiceException or JsonException)
        {
            _logger.LogWarning(ex, "Unable to resolve dead letter queue {DeadLetterQueueName} for {QueueName}: {Message}", deadLetterName, _options.Name, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Validates the options against SQS limits. The SQS emulators don't enforce these limits, and checking them here fails
    /// before <see cref="QueueBase{T,TOptions}.Enqueuing"/> handlers such as duplicate detection reserve state. It runs again
    /// after the handlers because they may change the options. The checks don't allocate.
    /// </summary>
    private void ValidateEntryOptions(QueueEntryOptions options)
    {
        ValidateSqsId(options.GroupId, nameof(QueueEntryOptions.GroupId));

        int attributeCount = CountMessageAttributes(options);
        if (attributeCount > MaxMessageAttributes)
            throw new QueueException($"SQS allows at most {MaxMessageAttributes} message attributes per message (including CorrelationId and TraceState), but {attributeCount} were provided.");

        if (!_isFifo)
            return;

        if (String.IsNullOrEmpty(options.GroupId))
            throw new QueueException($"A GroupId is required when enqueuing to the FIFO queue {_options.Name}.");

        if (options.DeliveryDelay > TimeSpan.Zero)
            throw new QueueException($"FIFO queue {_options.Name} does not support per-message delivery delays; configure DelaySeconds on the queue instead.");

        if (!String.IsNullOrEmpty(options.UniqueId))
            ValidateSqsId(options.UniqueId, nameof(QueueEntryOptions.UniqueId));
    }

    /// <summary>
    /// <c>MessageGroupId</c> and <c>MessageDeduplicationId</c> share the same SQS rules: 1-128 printable ASCII characters without spaces.
    /// </summary>
    private static void ValidateSqsId(string? value, string paramName)
    {
        if (value is null)
            return;

        if (value.Length is 0 or > MaxSqsIdLength)
            throw new ArgumentException($"{paramName} must be between 1 and {MaxSqsIdLength} characters for SQS.", paramName);

        foreach (char c in value)
        {
            if (c is < '!' or > '~')
                throw new ArgumentException($"{paramName} may only contain printable ASCII characters ('!' through '~') without spaces for SQS.", paramName);
        }
    }

    /// <summary>
    /// Counts the attributes <see cref="CreateMessageAttributes"/> would send. SQS attribute names are case-sensitive, so a
    /// property only replaces <c>CorrelationId</c> when its name matches exactly.
    /// </summary>
    private static int CountMessageAttributes(QueueEntryOptions options)
    {
        int count = options.Properties.Count;
        if (!String.IsNullOrEmpty(options.CorrelationId) && !HasCorrelationIdProperty(options.Properties))
            count++;

        return count;
    }

    /// <summary>
    /// Compares keys ordinally rather than with <see cref="IDictionary{TKey,TValue}.ContainsKey"/>, because the caller's
    /// dictionary may be case-insensitive while SQS attribute names are not.
    /// </summary>
    private static bool HasCorrelationIdProperty(IDictionary<string, string> properties)
    {
        foreach (string key in properties.Keys)
        {
            if (String.Equals(key, AttributeExtensions.CorrelationIdAttributeName, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Builds the SQS message attributes (custom properties plus <c>CorrelationId</c>), or <c>null</c> when there are none.
    /// <see cref="ValidateEntryOptions"/> enforces the attribute limit.
    /// </summary>
    private static Dictionary<string, MessageAttributeValue>? CreateMessageAttributes(QueueEntryOptions options)
    {
        bool hasCorrelationId = !String.IsNullOrEmpty(options.CorrelationId);
        if (options.Properties.Count is 0 && !hasCorrelationId)
            return null;

        var attributes = new Dictionary<string, MessageAttributeValue>(options.Properties.Count + 1, StringComparer.Ordinal);
        foreach (var property in options.Properties)
            attributes[property.Key] = new MessageAttributeValue { DataType = "String", StringValue = property.Value }; // TODO: Support more than string data types

        if (hasCorrelationId)
            attributes[AttributeExtensions.CorrelationIdAttributeName] = new MessageAttributeValue { DataType = "String", StringValue = options.CorrelationId };

        return attributes;
    }

    private async Task DeadLetterMessageAsync(SQSQueueEntry<T> entry)
    {
        _logger.LogInformation("Exceeded retry limit ({Attempts}/{Retries}), moving message {QueueEntryId} to dead letter", entry.Attempts, _options.Retries, entry.Id);

        var message = entry.UnderlyingMessage;
        string? deadLetterUrl = _options.SupportDeadLetter ? await ResolveDeadLetterUrlAsync().AnyContext() : null;
        if (deadLetterUrl is not null)
        {
            await _client.Value.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = deadLetterUrl,
                MessageBody = message.Body,
                // The received attribute dictionary is owned by this entry, which is deleted below, so it is reused rather than copied.
                MessageAttributes = message.MessageAttributes is { Count: > 0 } ? message.MessageAttributes : null,
                MessageGroupId = message.Attributes.MessageGroupId(),
                MessageDeduplicationId = _isFifo ? message.MessageId : null
            }).AnyContext();
        }
        else
        {
            _logger.LogWarning("No dead letter queue is configured for {QueueName}; discarding message {QueueEntryId}", _options.Name, entry.Id);
        }

        await _client.Value.DeleteMessageAsync(_queueUrl, message.ReceiptHandle).AnyContext();
    }

    /// <summary>
    /// Sets the <c>MessageDeduplicationId</c> of a FIFO message: the caller's <see cref="QueueEntryOptions.UniqueId"/>, or a
    /// generated id unless the queue derives one from the message body. SQS requires an id unless ContentBasedDeduplication is enabled.
    /// </summary>
    /// <returns><c>true</c> when no id was set because the queue uses content-based deduplication.</returns>
    private async Task<bool> SetFifoDeduplicationIdAsync(SendMessageRequest message, QueueEntryOptions options)
    {
        if (!String.IsNullOrEmpty(options.UniqueId))
        {
            message.MessageDeduplicationId = options.UniqueId;
            return false;
        }

        if (!_contentBasedDeduplicationResolved)
            await ResolveContentBasedDeduplicationAsync(message.QueueUrl).AnyContext();

        if (_contentBasedDeduplication)
            return true;

        message.MessageDeduplicationId = Guid.NewGuid().ToString("N");
        return false;
    }

    /// <summary>
    /// Reads the queue's ContentBasedDeduplication setting once per queue url, on the first FIFO send without a
    /// <see cref="QueueEntryOptions.UniqueId"/>, so instances that only consume or always set a UniqueId never read it.
    /// An explicit <c>MessageDeduplicationId</c> overrides content-based deduplication, so one is only generated when the
    /// queue doesn't derive it from the message body
    /// (<see href="https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_SendMessage.html">SendMessage</see>).
    /// </summary>
    /// <remarks>
    /// The setting isn't polled. If it is turned off later, SQS rejects the send and it is retried with a generated id
    /// (see <see cref="IsMissingDeduplicationIdError"/>). If it is turned on later, generated ids keep being sent, which is
    /// valid but bypasses content-based deduplication until the queue is reopened. If the read fails (for example a missing
    /// <c>sqs:GetQueueAttributes</c> permission), the next send omits the id and the outcome settles the setting: it succeeds
    /// when content-based deduplication is enabled, and otherwise is rejected and retried with a generated id. A failed read
    /// therefore costs one warning, not a call per send, and doesn't disable content-based deduplication.
    /// </remarks>
    private async Task ResolveContentBasedDeduplicationAsync(string queueUrl)
    {
        try
        {
            var response = await _client.Value.GetQueueAttributesAsync(new GetQueueAttributesRequest(queueUrl, [QueueAttributeName.ContentBasedDeduplication])).AnyContext();
            _contentBasedDeduplication = response.Attributes.TryGetValue(QueueAttributeName.ContentBasedDeduplication, out string? value)
                && Boolean.TryParse(value, out bool enabled) && enabled;
            _logger.LogDebug("Queue {QueueName} ContentBasedDeduplication is {ContentBasedDeduplication}", _options.Name, _contentBasedDeduplication);
        }
        catch (QueueDoesNotExistException)
        {
            ForgetQueueUrl(queueUrl);
            throw;
        }
        catch (AmazonSQSException ex)
        {
            _contentBasedDeduplication = true;
            _logger.LogWarning(ex, "Unable to read ContentBasedDeduplication for queue {QueueName}; the next send determines it: {Message}", _options.Name, ex.Message);
        }

        _contentBasedDeduplicationResolved = true;
    }

    /// <summary>
    /// SQS rejects a FIFO message without a deduplication id once content-based deduplication has been turned off.
    /// </summary>
    private static bool IsMissingDeduplicationIdError(AmazonSQSException ex)
    {
        return ex.ErrorCode is "InvalidParameterValue" && ex.Message.Contains("ContentBasedDeduplication", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// SQS deletes a queue asynchronously, so a url resolved while it was being deleted can be cached. Forget it once SQS
    /// reports the queue is gone so the next call resolves or recreates the queue instead of failing until restart.
    /// </summary>
    private void ForgetQueueUrl(string? queueUrl)
    {
        if (queueUrl is not null && Interlocked.CompareExchange(ref _queueUrl, null, queueUrl) == queueUrl)
            _contentBasedDeduplicationResolved = false;
    }

    /// <summary>
    /// Returns the dead letter queue the source queue's current redrive policy targets, but only when it follows the
    /// <c>{name}-deadletter</c> convention, so a shared or externally managed queue is never treated as owned. A queue
    /// that merely has the conventional name isn't enough: the source must point at it.
    /// </summary>
    /// <remarks>
    /// A failure (for example a principal allowed to delete queues but not to read their attributes) only skips the
    /// dead letter queue, so the source queue is still deleted.
    /// </remarks>
    private async Task<string?> FindPolicyDeadLetterUrlAsync(string queueUrl)
    {
        string ownedName = GetDeadLetterQueueName();
        try
        {
            var response = await _client.Value.GetQueueAttributesAsync(new GetQueueAttributesRequest(queueUrl, [QueueAttributeName.RedrivePolicy])).AnyContext();
            string? deadLetterName = response.Attributes.DeadLetterQueue();
            if (!String.Equals(deadLetterName, ownedName, StringComparison.Ordinal))
            {
                _logger.LogDebug("Dead letter queue {DeadLetterQueueName} of {QueueName} is not owned by this queue; it is not deleted", deadLetterName, _options.Name);
                return null;
            }

            return await FindQueueUrlAsync(ownedName).AnyContext();
        }
        catch (QueueDoesNotExistException)
        {
            return null;
        }
        catch (Exception ex) when (ex is AmazonServiceException or JsonException)
        {
            _logger.LogWarning(ex, "Unable to confirm the dead letter queue {DeadLetterQueueName} of {QueueName}; it is not deleted: {Message}", ownedName, _options.Name, ex.Message);
            return null;
        }
    }

    private async Task DeleteQueueIfExistsAsync(string queueUrl, string queueName)
    {
        try
        {
            await _client.Value.DeleteQueueAsync(queueUrl).AnyContext();
            _logger.LogDebug("Deleted queue {QueueName}", queueName);
        }
        catch (QueueDoesNotExistException)
        {
            _logger.LogDebug("Queue {QueueName} was already deleted", queueName);
        }
    }

    private QueueStats CreateQueueStats(long queued, long working, long deadletter = 0)
    {
        return new QueueStats
        {
            Queued = queued,
            Working = working,
            Deadletter = deadletter,
            Enqueued = _enqueuedCount,
            Dequeued = _dequeuedCount,
            Completed = _completedCount,
            Abandoned = _abandonedCount,
            Errors = _workerErrorCount,
            Timeouts = 0
        };
    }

    private sealed record DeadLetterQueue(string Name, string Url);

    private static SQSQueueEntry<T> ToQueueEntry(IQueueEntry<T> entry)
    {
        if (entry is not SQSQueueEntry<T> result)
            throw new ArgumentException($"Expected {nameof(SQSQueueEntry<T>)} but received unknown queue entry type {entry.GetType()}");

        return result;
    }
}

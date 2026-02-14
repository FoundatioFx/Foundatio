using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Foundatio.AsyncEx;
using Foundatio.AzureServiceBus.Queues;
using Foundatio.AzureServiceBus.Utility;
using Foundatio.Extensions;
using Foundatio.Serializer;
using Microsoft.Extensions.Logging;

namespace Foundatio.Queues;

public class AzureServiceBusQueue<T> : QueueBase<T, AzureServiceBusQueueOptions<T>> where T : class
{
    private readonly AsyncLock _lock = new();
    private readonly Lazy<ServiceBusClient> _client;
    private readonly Lazy<ServiceBusAdministrationClient> _adminClient;
    private readonly bool _isEmulator;
    private ServiceBusSender _queueSender;
    private ServiceBusReceiver _queueReceiver;

    private long _enqueuedCount;
    private long _dequeuedCount;
    private long _completedCount;
    private long _abandonedCount;
    private long _workerErrorCount;

    public AzureServiceBusQueue(AzureServiceBusQueueOptions<T> options) : base(options)
    {
        if (String.IsNullOrEmpty(options.ConnectionString) && String.IsNullOrEmpty(options.FullyQualifiedNamespace))
            throw new ArgumentException("ConnectionString or FullyQualifiedNamespace is required.");

        if (!String.IsNullOrEmpty(options.FullyQualifiedNamespace) && options.Credential == null)
            throw new ArgumentException("Credential is required when using FullyQualifiedNamespace.");

        if (options.Name.Length > 260)
            throw new ArgumentException("Queue name must be set and be less than 260 characters.");

        if (options.WorkItemTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentException("The maximum WorkItemTimeout value is 5 minutes; the default value is 1 minute.");

        if (options.AutoDeleteOnIdle.HasValue && options.AutoDeleteOnIdle < TimeSpan.FromMinutes(5))
            throw new ArgumentException("The minimum AutoDeleteOnIdle duration is 5 minutes.");

        if (options.DuplicateDetectionHistoryTimeWindow.HasValue && (options.DuplicateDetectionHistoryTimeWindow < TimeSpan.FromSeconds(20.0) || options.DuplicateDetectionHistoryTimeWindow > TimeSpan.FromDays(7.0)))
            throw new ArgumentException("The minimum DuplicateDetectionHistoryTimeWindow duration is 20 seconds and maximum is 7 days.");

        if (options.UserMetadata is { Length: > 1024 })
            throw new ArgumentException("Queue UserMetadata must be less than 1024 characters.");

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

    public AzureServiceBusQueue(Builder<AzureServiceBusQueueOptionsBuilder<T>, AzureServiceBusQueueOptions<T>> config)
        : this(config(new AzureServiceBusQueueOptionsBuilder<T>()).Build()) { }

    public ServiceBusClient Client => _client.Value;
    public ServiceBusAdministrationClient AdminClient => _adminClient.Value;
    public ServiceBusReceiver Receiver => _queueReceiver;
    public ServiceBusSender Sender => _queueSender;

    private bool QueueIsCreated => _queueReceiver != null && _queueSender != null;

    protected override async Task EnsureQueueCreatedAsync(CancellationToken cancellationToken = default)
    {
        if (QueueIsCreated)
            return;

        using (await _lock.LockAsync(cancellationToken).AnyContext())
        {
            if (QueueIsCreated)
                return;

            _logger.LogTrace("Ensuring queue {QueueName} exists", _options.Name);

            // Skip admin API calls when using the emulator - it doesn't support the management HTTP API
            if (!_isEmulator)
            {
                try
                {
                    bool queueExists = await _adminClient.Value.QueueExistsAsync(_options.Name, cancellationToken).AnyContext();
                    if (!queueExists)
                    {
                        if (!_options.CanCreateQueue)
                            throw new InvalidOperationException($"Queue {_options.Name} does not exist and CanCreateQueue is false.");

                        await _adminClient.Value.CreateQueueAsync(CreateQueueOptions(), cancellationToken).AnyContext();
                        _logger.LogDebug("Created queue {QueueName}", _options.Name);
                    }
                }
                catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityAlreadyExists)
                {
                    _logger.LogDebug(ex, "Queue {QueueName} already exists", _options.Name);
                }
            }
            else
            {
                _logger.LogDebug("Skipping queue existence check - using Azure Service Bus Emulator");
            }

            _queueSender = _client.Value.CreateSender(_options.Name);
            _queueReceiver = _client.Value.CreateReceiver(_options.Name, new ServiceBusReceiverOptions
            {
                ReceiveMode = ServiceBusReceiveMode.PeekLock
            });

            _logger.LogTrace("Queue {QueueName} setup complete", _options.Name);
        }
    }

    protected override async Task DeleteQueueImplAsync()
    {
        _logger.LogDebug("Deleting queue {QueueName}", _options.Name);

        // Skip admin API calls when using the emulator - it doesn't support the management HTTP API
        if (!_isEmulator)
        {
            try
            {
                bool queueExists = await _adminClient.Value.QueueExistsAsync(_options.Name).AnyContext();
                if (queueExists)
                    await _adminClient.Value.DeleteQueueAsync(_options.Name).AnyContext();
            }
            catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityNotFound)
            {
                _logger.LogDebug("Queue {QueueName} does not exist", _options.Name);
            }
        }
        else
        {
            _logger.LogDebug("Draining queue - using Azure Service Bus Emulator");
            // Drain all messages from the queue since we can't delete it
            await DrainQueueAsync().AnyContext();
        }

        if (_queueSender != null)
        {
            await _queueSender.DisposeAsync().AnyContext();
            _queueSender = null;
        }

        if (_queueReceiver != null)
        {
            await _queueReceiver.DisposeAsync().AnyContext();
            _queueReceiver = null;
        }

        _enqueuedCount = 0;
        _dequeuedCount = 0;
        _completedCount = 0;
        _abandonedCount = 0;
        _workerErrorCount = 0;
    }

    private async Task DrainQueueAsync()
    {
        await EnsureQueueCreatedAsync().AnyContext();

        int totalDrained = 0;
        int passes = 0;
        const int maxPasses = 5;
        const int maxWaitSeconds = 5;
        var startTime = DateTime.UtcNow;

        // Multiple passes to catch messages that may be in-flight or scheduled
        // We wait up to maxWaitSeconds for scheduled messages to become available
        while (passes < maxPasses && !DisposedCancellationToken.IsCancellationRequested)
        {
            int drained = 0;
            while (!DisposedCancellationToken.IsCancellationRequested)
            {
                var messages = await _queueReceiver.ReceiveMessagesAsync(100, TimeSpan.FromSeconds(1), DisposedCancellationToken).AnyContext();
                if (messages == null || messages.Count == 0)
                    break;

                foreach (var message in messages)
                {
                    if (DisposedCancellationToken.IsCancellationRequested)
                        break;

                    await _queueReceiver.CompleteMessageAsync(message, DisposedCancellationToken).AnyContext();
                    drained++;
                }
            }

            totalDrained += drained;
            passes++;

            // If we drained messages, continue draining
            if (drained > 0)
                continue;

            // No messages found - check if we should wait for scheduled messages
            double elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
            if (elapsed >= maxWaitSeconds)
                break;

            // Wait a bit for scheduled messages to become available
            try
            {
                await Task.Delay(500, DisposedCancellationToken).AnyContext();
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (totalDrained > 0)
            _logger.LogDebug("Drained {Count} messages from queue {QueueName}", totalDrained, _options.Name);
    }

    protected override async Task<QueueStats> GetQueueStatsImplAsync()
    {
        if (!QueueIsCreated)
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

        // Skip admin API calls when using the emulator - it doesn't support the management HTTP API
        if (_isEmulator)
        {
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
        }

        var properties = await _adminClient.Value.GetQueueRuntimePropertiesAsync(_options.Name).AnyContext();
        return new QueueStats
        {
            Queued = properties.Value.ActiveMessageCount,
            Working = 0,
            Deadletter = properties.Value.DeadLetterMessageCount,
            Enqueued = _enqueuedCount,
            Dequeued = _dequeuedCount,
            Completed = _completedCount,
            Abandoned = _abandonedCount,
            Errors = _workerErrorCount,
            Timeouts = 0
        };
    }

    protected override Task<IEnumerable<T>> GetDeadletterItemsImplAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    protected override async Task<string> EnqueueImplAsync(T data, QueueEntryOptions options)
    {
        if (!await OnEnqueuingAsync(data, options).AnyContext())
            return null;

        Interlocked.Increment(ref _enqueuedCount);

        var message = new ServiceBusMessage(_serializer.SerializeToBytes(data));

        if (!String.IsNullOrEmpty(options.UniqueId))
            message.MessageId = options.UniqueId;

        if (!String.IsNullOrEmpty(options.CorrelationId))
            message.CorrelationId = options.CorrelationId;

        if (options is AzureServiceBusQueueEntryOptions asbOptions && !String.IsNullOrEmpty(asbOptions.SessionId))
            message.SessionId = asbOptions.SessionId;

        if (options.DeliveryDelay.HasValue && options.DeliveryDelay.Value > TimeSpan.Zero)
            message.ScheduledEnqueueTime = DateTimeOffset.UtcNow.Add(options.DeliveryDelay.Value);

        if (options.Properties is not null)
        {
            foreach (var property in options.Properties)
                message.ApplicationProperties[property.Key] = property.Value;
        }

        _logger.LogTrace("Enqueuing message to queue {QueueName}", _options.Name);
        await _queueSender.SendMessageAsync(message).AnyContext();

        var entry = new QueueEntry<T>(message.MessageId, message.CorrelationId, data, this, _timeProvider.GetUtcNow().UtcDateTime, 0);

        foreach (var property in message.ApplicationProperties)
            entry.Properties.Add(property.Key, property.Value?.ToString());

        await OnEnqueuedAsync(entry).AnyContext();

        _logger.LogTrace("Enqueued message {MessageId} to queue {QueueName}", message.MessageId, _options.Name);
        return message.MessageId;
    }

    // TODO: See if we can simplify this.
    protected override async Task<IQueueEntry<T>> DequeueImplAsync(CancellationToken linkedCancellationToken)
    {
        // Calculate timeout based on cancellation state - like SQS pattern
        var timeout = linkedCancellationToken.IsCancellationRequested
            ? TimeSpan.FromMilliseconds(100)
            : _options.ReadQueueTimeout;

        _logger.LogTrace("Checking for message in queue {QueueName}... IsCancellationRequested={IsCancellationRequested} ReadQueueTimeout={ReadQueueTimeout}",
            _options.Name, linkedCancellationToken.IsCancellationRequested, timeout);

        ServiceBusReceivedMessage message = null;

        // Initial receive attempt - try at least once even if cancellation is requested
        // This supports the pattern where TimeSpan.Zero is passed to mean "check once without waiting"
        try
        {
            message = await _queueReceiver.ReceiveMessageAsync(timeout, CancellationToken.None).AnyContext();
        }
        catch (OperationCanceledException)
        {
            _logger.LogTrace("Operation cancelled while receiving message");
            return null;
        }

        // Retry loop
        while (message == null && !linkedCancellationToken.IsCancellationRequested)
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
                    return null;
                }
            }

            try
            {
                message = await _queueReceiver.ReceiveMessageAsync(_options.ReadQueueTimeout, linkedCancellationToken).AnyContext();
            }
            catch (OperationCanceledException)
            {
                _logger.LogTrace("Operation cancelled while receiving message");
                return null;
            }
        }

        if (message == null)
        {
            _logger.LogTrace("No message received from queue {QueueName} IsCancellationRequested={IsCancellationRequested}",
                _options.Name, linkedCancellationToken.IsCancellationRequested);
            return null;
        }

        Interlocked.Increment(ref _dequeuedCount);
        _logger.LogTrace("Received message {MessageId} from queue {QueueName} IsCancellationRequested={IsCancellationRequested}",
            message.MessageId, _options.Name, linkedCancellationToken.IsCancellationRequested);

        var data = _serializer.Deserialize<T>(message.Body.ToArray());
        var entry = new AzureServiceBusQueueEntry<T>(message, data, this);

        await OnDequeuedAsync(entry).AnyContext();
        return entry;
    }

    public override async Task RenewLockAsync(IQueueEntry<T> queueEntry)
    {
        _logger.LogDebug("Queue {QueueName} renew lock item: {QueueEntryId}", _options.Name, queueEntry.Id);

        var entry = ToQueueEntry(queueEntry);
        await _queueReceiver.RenewMessageLockAsync(entry.UnderlyingMessage).AnyContext();

        await OnLockRenewedAsync(entry).AnyContext();
        _logger.LogTrace("Renew lock done: {QueueEntryId} MessageId={MessageId}", queueEntry.Id, entry.UnderlyingMessage.MessageId);
    }

    public override async Task CompleteAsync(IQueueEntry<T> queueEntry)
    {
        _logger.LogDebug("Queue {QueueName} complete item: {QueueEntryId}", _options.Name, queueEntry.Id);

        if (queueEntry.IsAbandoned || queueEntry.IsCompleted)
            throw new InvalidOperationException("Queue entry has already been completed or abandoned.");

        var entry = ToQueueEntry(queueEntry);
        await _queueReceiver.CompleteMessageAsync(entry.UnderlyingMessage).AnyContext();

        Interlocked.Increment(ref _completedCount);
        queueEntry.MarkCompleted();
        await OnCompletedAsync(queueEntry).AnyContext();
        _logger.LogTrace("Complete done: {QueueEntryId} MessageId={MessageId}", queueEntry.Id, entry.UnderlyingMessage.MessageId);
    }

    // TODO: See if we need to handle retries.
    public override async Task AbandonAsync(IQueueEntry<T> queueEntry)
    {
        _logger.LogDebug("Queue {QueueName} ({QueueId}) abandon item: {QueueEntryId}", _options.Name, QueueId, queueEntry.Id);

        if (queueEntry.IsAbandoned || queueEntry.IsCompleted)
            throw new InvalidOperationException("Queue entry has already been completed or abandoned.");

        var entry = ToQueueEntry(queueEntry);

        // Calculate retry delay based on attempt number
        var retryDelay = _options.RetryDelay(entry.Attempts);

        // Azure Service Bus doesn't support delayed retry on abandon natively.
        // If retry delay is specified, we complete the message and re-enqueue with scheduled delivery.
        if (retryDelay > TimeSpan.Zero)
        {
            _logger.LogTrace("Scheduling retry for queue entry: {QueueEntryId} MessageId={MessageId} RetryDelay={RetryDelay} Attempts={Attempts}",
                entry.Id, entry.UnderlyingMessage.MessageId, retryDelay, entry.Attempts);

            // Create a new message with same content for scheduled retry
            var retryMessage = new ServiceBusMessage(entry.UnderlyingMessage.Body)
            {
                MessageId = entry.UnderlyingMessage.MessageId,
                CorrelationId = entry.UnderlyingMessage.CorrelationId,
                ScheduledEnqueueTime = DateTimeOffset.UtcNow.Add(retryDelay)
            };

            // Copy application properties (excluding SDK diagnostic properties)
            foreach (var prop in entry.UnderlyingMessage.ApplicationProperties)
            {
                if (!ServiceBusMessageHelper.IsSdkDiagnosticProperty(prop.Key))
                    retryMessage.ApplicationProperties[prop.Key] = prop.Value;
            }

            // Store attempt count for next dequeue
            retryMessage.ApplicationProperties["_attempts"] = entry.Attempts;

            // Complete the original message and schedule the retry
            await _queueReceiver.CompleteMessageAsync(entry.UnderlyingMessage).AnyContext();
            await _queueSender.SendMessageAsync(retryMessage).AnyContext();
        }
        else
        {
            // No retry delay - just abandon for immediate retry
            await _queueReceiver.AbandonMessageAsync(entry.UnderlyingMessage).AnyContext();
        }

        _logger.LogTrace("Abandoned queue entry: {QueueEntryId} MessageId={MessageId} RetryDelay={RetryDelay}",
            entry.Id, entry.UnderlyingMessage.MessageId, retryDelay);

        Interlocked.Increment(ref _abandonedCount);
        queueEntry.MarkAbandoned();

        await OnAbandonedAsync(queueEntry).AnyContext();
        _logger.LogTrace("Abandon complete: {QueueEntryId}", queueEntry.Id);
    }

    protected override void StartWorkingImpl(Func<IQueueEntry<T>, CancellationToken, Task> handler, bool autoComplete, CancellationToken cancellationToken)
    {
        if (handler == null)
            throw new ArgumentNullException(nameof(handler));

        var linkedCancellationToken = GetLinkedDisposableCancellationTokenSource(cancellationToken);

        Task.Run(async () =>
        {
            _logger.LogTrace("WorkerLoop Start {QueueName}", _options.Name);

            while (!linkedCancellationToken.IsCancellationRequested)
            {
                _logger.LogTrace("WorkerLoop Signaled {QueueName}", _options.Name);

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
                    _logger.LogError(ex, "Worker error: {Message}", ex.Message);

                    if (!entry.IsAbandoned && !entry.IsCompleted && !linkedCancellationToken.IsCancellationRequested)
                        await entry.AbandonAsync().AnyContext();
                }
            }

            _logger.LogTrace("Worker exiting: {QueueName} IsCancellationRequested={IsCancellationRequested}", _options.Name, linkedCancellationToken.IsCancellationRequested);
        }, linkedCancellationToken.Token).ContinueWith(_ => linkedCancellationToken.Dispose());
    }

    private CreateQueueOptions CreateQueueOptions()
    {
        var options = new CreateQueueOptions(_options.Name)
        {
            LockDuration = _options.WorkItemTimeout,
            MaxDeliveryCount = _options.Retries + 1
        };

        if (_options.AutoDeleteOnIdle.HasValue)
            options.AutoDeleteOnIdle = _options.AutoDeleteOnIdle.Value;

        if (_options.DefaultMessageTimeToLive.HasValue)
            options.DefaultMessageTimeToLive = _options.DefaultMessageTimeToLive.Value;

        if (_options.DuplicateDetectionHistoryTimeWindow.HasValue)
            options.DuplicateDetectionHistoryTimeWindow = _options.DuplicateDetectionHistoryTimeWindow.Value;

        if (_options.EnableBatchedOperations.HasValue)
            options.EnableBatchedOperations = _options.EnableBatchedOperations.Value;

        if (_options.EnableDeadLetteringOnMessageExpiration.HasValue)
            options.DeadLetteringOnMessageExpiration = _options.EnableDeadLetteringOnMessageExpiration.Value;

        if (_options.EnablePartitioning.HasValue)
            options.EnablePartitioning = _options.EnablePartitioning.Value;

        if (!String.IsNullOrEmpty(_options.ForwardDeadLetteredMessagesTo))
            options.ForwardDeadLetteredMessagesTo = _options.ForwardDeadLetteredMessagesTo;

        if (!String.IsNullOrEmpty(_options.ForwardTo))
            options.ForwardTo = _options.ForwardTo;

        if (_options.MaxSizeInMegabytes.HasValue)
            options.MaxSizeInMegabytes = _options.MaxSizeInMegabytes.Value;

        if (_options.RequiresDuplicateDetection.HasValue)
            options.RequiresDuplicateDetection = _options.RequiresDuplicateDetection.Value;

        if (_options.RequiresSession.HasValue)
            options.RequiresSession = _options.RequiresSession.Value;

        if (_options.Status.HasValue)
            options.Status = _options.Status.Value;

        if (!String.IsNullOrEmpty(_options.UserMetadata))
            options.UserMetadata = _options.UserMetadata;

        return options;
    }

    public override void Dispose()
    {
        // TODO: Improve Async Cleanup
        base.Dispose();

        if (_queueSender != null)
        {
            _queueSender.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _queueSender = null;
        }

        if (_queueReceiver != null)
        {
            _queueReceiver.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _queueReceiver = null;
        }

        if (_client.IsValueCreated)
        {
            _client.Value.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static AzureServiceBusQueueEntry<T> ToQueueEntry(IQueueEntry<T> entry)
    {
        if (entry is not AzureServiceBusQueueEntry<T> result)
            throw new ArgumentException($"Expected {nameof(AzureServiceBusQueueEntry<T>)} but received unknown queue entry type {entry.GetType()}");

        return result;
    }
}

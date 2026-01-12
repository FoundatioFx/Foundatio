using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Foundatio.AsyncEx;
using Foundatio.Extensions;
using Foundatio.Serializer;
using Microsoft.Extensions.Logging;

namespace Foundatio.Queues;

public class AzureStorageQueue<T> : QueueBase<T, AzureStorageQueueOptions<T>> where T : class
{
    private readonly AsyncLock _lock = new();
    private readonly Lazy<QueueClient> _queueClient;
    private readonly Lazy<QueueClient> _deadletterQueueClient;
    private long _enqueuedCount;
    private long _dequeuedCount;
    private long _completedCount;
    private long _abandonedCount;
    private long _workerErrorCount;
    private bool _queueCreated;

    public AzureStorageQueue(AzureStorageQueueOptions<T> options) : base(options)
    {
        if (String.IsNullOrEmpty(options.ConnectionString))
            throw new ArgumentException("ConnectionString is required.");

        var clientOptions = new QueueClientOptions();
        if (options.CompatibilityMode == AzureStorageQueueCompatibilityMode.Legacy)
            clientOptions.MessageEncoding = QueueMessageEncoding.Base64;

        options.ConfigureRetry?.Invoke(clientOptions.Retry);

        _queueClient = new Lazy<QueueClient>(() =>
            new QueueClient(options.ConnectionString, _options.Name, clientOptions));
        _deadletterQueueClient = new Lazy<QueueClient>(() =>
            new QueueClient(options.ConnectionString, $"{_options.Name}-poison", clientOptions));
    }

    public AzureStorageQueue(Builder<AzureStorageQueueOptionsBuilder<T>, AzureStorageQueueOptions<T>> config)
        : this(config(new AzureStorageQueueOptionsBuilder<T>()).Build())
    {
    }

    protected override async Task EnsureQueueCreatedAsync(CancellationToken cancellationToken = default)
    {
        if (_queueCreated)
            return;

        using (await _lock.LockAsync(DisposedCancellationToken).AnyContext())
        {
            if (_queueCreated)
                return;

            var sw = Stopwatch.StartNew();
            await Task.WhenAll(
                _queueClient.Value.CreateIfNotExistsAsync(cancellationToken: cancellationToken),
                _deadletterQueueClient.Value.CreateIfNotExistsAsync(cancellationToken: cancellationToken)
            ).AnyContext();
            _queueCreated = true;

            sw.Stop();
            _logger.LogTrace("Ensure queue exists took {Elapsed:g}", sw.Elapsed);
        }
    }

    protected override async Task<string> EnqueueImplAsync(T data, QueueEntryOptions options)
    {
        if (!await OnEnqueuingAsync(data, options).AnyContext())
            return null;

        Interlocked.Increment(ref _enqueuedCount);

        byte[] messageBodyBytes;

        if (_options.CompatibilityMode == AzureStorageQueueCompatibilityMode.Default)
        {
            // Wrap in envelope to preserve CorrelationId and Properties
            var envelope = new QueueMessageEnvelope<T>
            {
                CorrelationId = options.CorrelationId,
                Properties = options.Properties,
                Data = data
            };
            messageBodyBytes = _serializer.SerializeToBytes(envelope);
        }
        else
        {
            // Legacy mode: serialize data directly for backward compatibility
            messageBodyBytes = _serializer.SerializeToBytes(data);
        }

        var messageBody = new BinaryData(messageBodyBytes);
        var response = await _queueClient.Value.SendMessageAsync(
            messageBody,
            visibilityTimeout: options.DeliveryDelay,
            cancellationToken: CancellationToken.None).AnyContext();

        var entry = new QueueEntry<T>(response.Value.MessageId, null, data, this, _timeProvider.GetLocalNow().UtcDateTime, 0);
        await OnEnqueuedAsync(entry).AnyContext();

        _logger.LogTrace("Enqueued message {MessageId}", response.Value.MessageId);
        return response.Value.MessageId;
    }

    protected override async Task<IQueueEntry<T>> DequeueImplAsync(CancellationToken linkedCancellationToken)
    {
        _logger.LogTrace("Checking for message: IsCancellationRequested={IsCancellationRequested} VisibilityTimeout={VisibilityTimeout}", linkedCancellationToken.IsCancellationRequested, _options.WorkItemTimeout);

        // Try to receive a message immediately
        // Note: We use CancellationToken.None here because Azure SDK throws TaskCanceledException when the token
        // is canceled, but we want to return null gracefully. Cancellation is checked before/after the call.
        var response = await _queueClient.Value.ReceiveMessageAsync(_options.WorkItemTimeout, CancellationToken.None).AnyContext();
        var message = response?.Value;

        // If no message and cancellation requested, return null immediately (don't wait/poll)
        if (message == null && linkedCancellationToken.IsCancellationRequested)
        {
            _logger.LogTrace("No message available and cancellation requested, returning null");
            return null;
        }

        // If no message and not canceled, poll with fixed interval
        // Note: Azure Storage Queue doesn't support long-polling, so we must poll
        if (message == null)
        {
            var sw = Stopwatch.StartNew();
            var lastReport = _timeProvider.GetUtcNow();
            _logger.LogTrace("No message available to dequeue, polling...");

            while (message == null && !linkedCancellationToken.IsCancellationRequested)
            {
                if (_timeProvider.GetUtcNow().Subtract(lastReport) > TimeSpan.FromSeconds(10))
                {
                    lastReport = _timeProvider.GetUtcNow();
                    _logger.LogTrace("Still waiting for message to dequeue: {Elapsed:g}", sw.Elapsed);
                }

                try
                {
                    if (!linkedCancellationToken.IsCancellationRequested)
                        await _timeProvider.Delay(_options.DequeueInterval, linkedCancellationToken).AnyContext();
                }
                catch (OperationCanceledException)
                {
                    // Ignore cancellation during delay
                }

                if (linkedCancellationToken.IsCancellationRequested)
                    break;

                response = await _queueClient.Value.ReceiveMessageAsync(_options.WorkItemTimeout, CancellationToken.None).AnyContext();
                message = response?.Value;
            }

            sw.Stop();
            _logger.LogTrace("Waited to dequeue message: {Elapsed:g}", sw.Elapsed);
        }

        if (message == null)
        {
            _logger.LogTrace("No message was dequeued");
            return null;
        }

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var insertedOn = message.InsertedOn?.UtcDateTime ?? DateTime.MinValue;
        var queueTime = nowUtc - insertedOn;
        _logger.LogTrace("Received message: {QueueEntryId} InsertedOn={InsertedOn} NowUtc={NowUtc} QueueTime={QueueTime}ms IsCancellationRequested={IsCancellationRequested}",
            message.MessageId, insertedOn, nowUtc, queueTime.TotalMilliseconds, linkedCancellationToken.IsCancellationRequested);
        Interlocked.Increment(ref _dequeuedCount);

        T data;
        string correlationId = null;
        IDictionary<string, string> properties = null;

        if (_options.CompatibilityMode == AzureStorageQueueCompatibilityMode.Default)
        {
            // Unwrap envelope to extract metadata
            var envelope = _serializer.Deserialize<QueueMessageEnvelope<T>>(message.Body.ToArray());
            data = envelope.Data;
            correlationId = envelope.CorrelationId;
            properties = envelope.Properties;
        }
        else
        {
            // Legacy mode: deserialize data directly (no envelope)
            data = _serializer.Deserialize<T>(message.Body.ToArray());
        }

        var entry = new AzureStorageQueueEntry<T>(message, correlationId, properties, data, this);

        await OnDequeuedAsync(entry).AnyContext();
        _logger.LogTrace("Dequeued message: {QueueEntryId}", message.MessageId);
        return entry;
    }

    public override async Task RenewLockAsync(IQueueEntry<T> entry)
    {
        _logger.LogDebug("Queue {QueueName} renew lock item: {QueueEntryId}", _options.Name, entry.Id);
        var azureQueueEntry = ToAzureEntryWithCheck(entry);
        var response = await _queueClient.Value.UpdateMessageAsync(
            azureQueueEntry.UnderlyingMessage.MessageId,
            azureQueueEntry.PopReceipt,
            visibilityTimeout: _options.WorkItemTimeout).AnyContext();

        // Update the pop receipt since it changes after each update
        azureQueueEntry.PopReceipt = response.Value.PopReceipt;

        await OnLockRenewedAsync(entry).AnyContext();
        _logger.LogTrace("Renew lock done: {QueueEntryId} MessageId={MessageId} VisibilityTimeout={VisibilityTimeout}", entry.Id, azureQueueEntry.UnderlyingMessage.MessageId, _options.WorkItemTimeout);
    }

    public override async Task CompleteAsync(IQueueEntry<T> entry)
    {
        _logger.LogDebug("Queue {QueueName} complete item: {QueueEntryId}", _options.Name, entry.Id);
        if (entry.IsAbandoned || entry.IsCompleted)
            throw new InvalidOperationException("Queue entry has already been completed or abandoned.");

        var azureQueueEntry = ToAzureEntryWithCheck(entry);

        try
        {
            await _queueClient.Value.DeleteMessageAsync(
                azureQueueEntry.UnderlyingMessage.MessageId,
                azureQueueEntry.PopReceipt).AnyContext();
        }
        catch (RequestFailedException ex) when (ex.Status == 404 || ex.ErrorCode == QueueErrorCode.MessageNotFound || ex.ErrorCode == QueueErrorCode.PopReceiptMismatch)
        {
            // Message was already deleted or the pop receipt expired (visibility timeout)
            // This means the item was auto-abandoned by Azure
            _logger.LogWarning("Failed to complete message {MessageId}: message not found or pop receipt expired (visibility timeout may have elapsed)", azureQueueEntry.UnderlyingMessage.MessageId);
            throw new InvalidOperationException("Queue entry visibility timeout has elapsed and the message is no longer locked.", ex);
        }

        Interlocked.Increment(ref _completedCount);
        entry.MarkCompleted();
        await OnCompletedAsync(entry).AnyContext();
        _logger.LogTrace("Complete done: {QueueEntryId}", entry.Id);
    }

    public override async Task AbandonAsync(IQueueEntry<T> entry)
    {
        _logger.LogDebug("Queue {QueueName} ({QueueId}) abandon item: {QueueEntryId}", _options.Name, QueueId, entry.Id);
        if (entry.IsAbandoned || entry.IsCompleted)
            throw new InvalidOperationException("Queue entry has already been completed or abandoned.");

        var azureQueueEntry = ToAzureEntryWithCheck(entry);
        if (azureQueueEntry.Attempts > _options.Retries)
        {
            _logger.LogDebug("Moving message {QueueEntryId} to deadletter after {Attempts} attempts", entry.Id, azureQueueEntry.Attempts);

            // Send to deadletter queue first, then delete from main queue (sequential for data integrity)
            byte[] deadletterBytes;
            if (_options.CompatibilityMode == AzureStorageQueueCompatibilityMode.Default)
            {
                // Preserve envelope with metadata for Default mode
                var envelope = new QueueMessageEnvelope<T>
                {
                    CorrelationId = entry.CorrelationId,
                    Properties = entry.Properties,
                    Data = entry.Value
                };
                deadletterBytes = _serializer.SerializeToBytes(envelope);
            }
            else
            {
                deadletterBytes = _serializer.SerializeToBytes(entry.Value);
            }

            var messageBody = new BinaryData(deadletterBytes);
            await _deadletterQueueClient.Value.SendMessageAsync(messageBody).AnyContext();
            await _queueClient.Value.DeleteMessageAsync(
                azureQueueEntry.UnderlyingMessage.MessageId,
                azureQueueEntry.PopReceipt).AnyContext();
        }
        else
        {
            // Calculate visibility timeout based on retry delay
            var retryDelay = _options.RetryDelay(azureQueueEntry.Attempts);
            _logger.LogDebug("Making message {QueueEntryId} visible after {RetryDelay}", entry.Id, retryDelay);

            var response = await _queueClient.Value.UpdateMessageAsync(
                azureQueueEntry.UnderlyingMessage.MessageId,
                azureQueueEntry.PopReceipt,
                visibilityTimeout: retryDelay).AnyContext();

            // Update the pop receipt since it changes after each update
            azureQueueEntry.PopReceipt = response.Value.PopReceipt;
        }

        Interlocked.Increment(ref _abandonedCount);
        entry.MarkAbandoned();
        await OnAbandonedAsync(entry).AnyContext();
        _logger.LogTrace("Abandon complete: {QueueEntryId} MessageId={MessageId} Attempts={Attempts}", entry.Id, azureQueueEntry.UnderlyingMessage.MessageId, azureQueueEntry.Attempts);
    }

    protected override Task<IEnumerable<T>> GetDeadletterItemsImplAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException("Azure Storage Queues do not support retrieving the entire queue");
    }

    protected override async Task<QueueStats> GetQueueStatsImplAsync()
    {
        // Note: Azure Storage Queue does not provide Working count (in-flight messages) or Timeouts.
        // These stats are only available per-process and meaningless in distributed scenarios.
        if (!_queueCreated)
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

        var sw = Stopwatch.StartNew();
        var queuePropertiesTask = _queueClient.Value.GetPropertiesAsync();
        var deadLetterPropertiesTask = _deadletterQueueClient.Value.GetPropertiesAsync();
        await Task.WhenAll(queuePropertiesTask, deadLetterPropertiesTask).AnyContext();
        sw.Stop();
        _logger.LogTrace("Fetching stats took {Elapsed:g}", sw.Elapsed);

        return new QueueStats
        {
            Queued = queuePropertiesTask.Result.Value.ApproximateMessagesCount,
            Working = 0, // Azure Storage Queue does not expose in-flight message count
            Deadletter = deadLetterPropertiesTask.Result.Value.ApproximateMessagesCount,
            Enqueued = _enqueuedCount,
            Dequeued = _dequeuedCount,
            Completed = _completedCount,
            Abandoned = _abandonedCount,
            Errors = _workerErrorCount,
            Timeouts = 0 // Azure handles visibility timeout natively; client-side tracking not meaningful
        };
    }

    protected override QueueStats GetMetricsQueueStats()
    {
        if (!_queueCreated)
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

        var sw = Stopwatch.StartNew();
        var queueProperties = _queueClient.Value.GetProperties();
        var deadletterProperties = _deadletterQueueClient.Value.GetProperties();
        sw.Stop();
        _logger.LogTrace("Fetching stats took {Elapsed:g}", sw.Elapsed);

        return new QueueStats
        {
            Queued = queueProperties.Value.ApproximateMessagesCount,
            Working = 0,
            Deadletter = deadletterProperties.Value.ApproximateMessagesCount,
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
        var sw = Stopwatch.StartNew();
        await Task.WhenAll(
            _queueClient.Value.DeleteIfExistsAsync(),
            _deadletterQueueClient.Value.DeleteIfExistsAsync()
        ).AnyContext();
        _queueCreated = false;

        _enqueuedCount = 0;
        _dequeuedCount = 0;
        _completedCount = 0;
        _abandonedCount = 0;
        _workerErrorCount = 0;

        sw.Stop();
        _logger.LogTrace("Deleting queue took {Elapsed:g}", sw.Elapsed);
    }

    protected override void StartWorkingImpl(Func<IQueueEntry<T>, CancellationToken, Task> handler, bool autoComplete, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var linkedCancellationToken = GetLinkedDisposableCancellationTokenSource(cancellationToken);

        Task.Run(async () =>
        {
            _logger.LogTrace("WorkerLoop Start {QueueName}", _options.Name);

            while (!linkedCancellationToken.IsCancellationRequested)
            {
                _logger.LogTrace("WorkerLoop Signaled {QueueName}", _options.Name);

                IQueueEntry<T> queueEntry = null;
                try
                {
                    queueEntry = await DequeueImplAsync(linkedCancellationToken.Token).AnyContext();
                }
                catch (OperationCanceledException)
                {
                    // Ignore cancellation
                }

                if (linkedCancellationToken.IsCancellationRequested || queueEntry == null)
                    continue;

                try
                {
                    await handler(queueEntry, linkedCancellationToken.Token).AnyContext();
                    if (autoComplete && !queueEntry.IsAbandoned && !queueEntry.IsCompleted)
                        await queueEntry.CompleteAsync().AnyContext();
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _workerErrorCount);
                    _logger.LogError(ex, "Worker error: {Message}", ex.Message);

                    if (!queueEntry.IsAbandoned && !queueEntry.IsCompleted)
                        await queueEntry.AbandonAsync().AnyContext();
                }
            }

            _logger.LogTrace("Worker exiting: {QueueName} Cancel Requested: {IsCancellationRequested}", _options.Name, linkedCancellationToken.IsCancellationRequested);
        }, linkedCancellationToken.Token).ContinueWith(t => linkedCancellationToken.Dispose());
    }

    private static AzureStorageQueueEntry<T> ToAzureEntryWithCheck(IQueueEntry<T> queueEntry)
    {
        if (queueEntry is not AzureStorageQueueEntry<T> azureQueueEntry)
            throw new ArgumentException($"Unknown entry type. Can only process entries of type '{nameof(AzureStorageQueueEntry<T>)}'");

        return azureQueueEntry;
    }
}

/// <summary>
/// Envelope for wrapping queue data with metadata (correlation id, properties).
/// Azure Storage Queue only supports a message body, so we serialize this entire envelope.
/// </summary>
internal record QueueMessageEnvelope<T> where T : class
{
    /// <summary>
    /// Correlation ID for distributed tracing
    /// </summary>
    public string CorrelationId { get; init; }

    /// <summary>
    /// Custom properties/metadata
    /// </summary>
    public IDictionary<string, string> Properties { get; init; }

    /// <summary>
    /// The actual message payload
    /// </summary>
    public T Data { get; init; }
}

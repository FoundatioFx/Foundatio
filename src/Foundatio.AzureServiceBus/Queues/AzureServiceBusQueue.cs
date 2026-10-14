using System;
using System.Collections.Concurrent;
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

public class AzureServiceBusQueue<T> : QueueBase<T, AzureServiceBusQueueOptions<T>>, IAsyncDisposable where T : class
{
    private const int DrainBatchSize = 250;
    private static readonly TimeSpan DrainReceiveWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxScheduledDrainWait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Minimum wait after a failed receive in the worker loop. The SDK already retries transient errors
    /// (see <see href="https://learn.microsoft.com/azure/service-bus-messaging/service-bus-retry-guidance">retry guidance</see>),
    /// so errors that reach the worker are usually persistent; this keeps them from becoming a tight request loop.
    /// </summary>
    private static readonly TimeSpan MinDequeueErrorBackoff = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Minimum time to accept a session and then receive from it. Accepting a session opens a link, which needs at least one
    /// network round trip, so a check-once dequeue (cancelled token, 100ms) would otherwise miss messages that are available.
    /// </summary>
    private static readonly TimeSpan MinSessionReceiveWait = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Service Bus limit for <see cref="ServiceBusMessage.SessionId"/>.
    /// See <see href="https://learn.microsoft.com/azure/service-bus-messaging/service-bus-quotas">Service Bus quotas</see>.
    /// </summary>
    private const int MaxSessionIdLength = 128;

    /// <summary>
    /// Margin before an unsettled entry whose lock appears expired is released, so a local clock that runs ahead of the
    /// broker doesn't close a session that a handler still holds.
    /// </summary>
    private static readonly TimeSpan ExpiredLockGracePeriod = TimeSpan.FromSeconds(30);

    private bool? _queueRequiresDuplicateDetection;

    private readonly AsyncLock _lock = new();
    private readonly ConcurrentDictionary<long, AzureServiceBusQueueEntry<T>> _unsettledEntries = new();
    private readonly Lazy<ServiceBusClient> _client;
    private readonly Lazy<ServiceBusAdministrationClient> _adminClient;
    private readonly bool _isEmulator;
    private ServiceBusSender? _queueSender;
    private ServiceBusReceiver? _queueReceiver;

    private long _enqueuedCount;
    private long _dequeuedCount;
    private long _completedCount;
    private long _abandonedCount;
    private long _workerErrorCount;

    public AzureServiceBusQueue(AzureServiceBusQueueOptions<T> options) : base(options)
    {
        if (String.IsNullOrEmpty(options.ConnectionString) && String.IsNullOrEmpty(options.FullyQualifiedNamespace))
            throw new ArgumentException("ConnectionString or FullyQualifiedNamespace is required.");

        if (!String.IsNullOrEmpty(options.FullyQualifiedNamespace) && options.Credential is null)
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
    public ServiceBusReceiver? Receiver => _queueReceiver;
    public ServiceBusSender? Sender => _queueSender;

    private bool QueueIsCreated => _queueReceiver is not null && _queueSender is not null;

    private bool RequiresSession => _options.RequiresSession is true;

    protected override bool SupportsGroupId => RequiresSession;

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
                            throw new QueueException($"Queue {_options.Name} does not exist and CanCreateQueue is false.");

                        var created = await _adminClient.Value.CreateQueueAsync(CreateQueueOptions(), cancellationToken).AnyContext();
                        _queueRequiresDuplicateDetection = created.Value.RequiresDuplicateDetection;
                        _logger.LogDebug("Created queue {QueueName}", _options.Name);
                    }
                    else
                    {
                        // Delayed retries are disabled on queues with duplicate detection, so use the existing queue's setting.
                        var properties = await _adminClient.Value.GetQueueAsync(_options.Name, cancellationToken).AnyContext();
                        _queueRequiresDuplicateDetection = properties.Value.RequiresDuplicateDetection;
                    }
                }
                catch (ServiceBusException ex) when (ex.Reason is ServiceBusFailureReason.MessagingEntityAlreadyExists)
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

        await CompleteUnsettledEntriesAsync().AnyContext();

        // Skip admin API calls when using the emulator - it doesn't support the management HTTP API
        if (!_isEmulator)
        {
            try
            {
                bool queueExists = await _adminClient.Value.QueueExistsAsync(_options.Name).AnyContext();
                if (queueExists)
                    await _adminClient.Value.DeleteQueueAsync(_options.Name).AnyContext();
            }
            catch (ServiceBusException ex) when (ex.Reason is ServiceBusFailureReason.MessagingEntityNotFound)
            {
                _logger.LogDebug("Queue {QueueName} does not exist", _options.Name);
            }
        }
        else
        {
            _logger.LogDebug("Draining queue - using Azure Service Bus Emulator");
            try
            {
                await DrainQueueAsync().AnyContext();
            }
            catch (ServiceBusException ex) when (ex.Reason is ServiceBusFailureReason.MessagingEntityNotFound)
            {
                _logger.LogDebug(ex, "Queue {QueueName} not found while draining (emulator); treating as empty", _options.Name);
            }
        }

        if (_queueSender is not null)
        {
            await _queueSender.DisposeAsync().AnyContext();
            _queueSender = null;
        }

        if (_queueReceiver is not null)
        {
            await _queueReceiver.DisposeAsync().AnyContext();
            _queueReceiver = null;
        }

        _queueRequiresDuplicateDetection = null;
        _enqueuedCount = 0;
        _dequeuedCount = 0;
        _completedCount = 0;
        _abandonedCount = 0;
        _workerErrorCount = 0;
    }

    /// <summary>
    /// Completes messages this instance dequeued but never settled. A drain cannot receive them while
    /// they are locked, so without this they would reappear after the delete once their lock expires.
    /// Stops early when the queue is disposed; disposing the client closes the remaining receivers.
    /// </summary>
    private async Task CompleteUnsettledEntriesAsync()
    {
        foreach (long sequenceNumber in _unsettledEntries.Keys)
        {
            if (DisposedCancellationToken.IsCancellationRequested)
                break;

            if (!_unsettledEntries.TryRemove(sequenceNumber, out var entry))
                continue;

            var receiver = GetReceiver(entry);
            var message = entry.UnderlyingMessage;
            try
            {
                await receiver.CompleteMessageAsync(message, DisposedCancellationToken).AnyContext();
                ((IQueueEntry)entry).MarkCompleted();
            }
            catch (ServiceBusException ex) when (ex.Reason is ServiceBusFailureReason.MessageLockLost or ServiceBusFailureReason.SessionLockLost)
            {
                _logger.LogDebug(ex, "Lock already released for unsettled message {MessageId} in queue {QueueName}: {Message}", message.MessageId, _options.Name, ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One entry failing to settle must not stop the others. Releasing its lock lets the drain that follows
                // receive and remove it instead of it reappearing once the lock expires.
                _logger.LogWarning(ex, "Unable to complete unsettled message {MessageId} in queue {QueueName}; abandoning it so it can be drained: {Message}", message.MessageId, _options.Name, ex.Message);
                await TryAbandonUnsettledAsync(receiver, message).AnyContext();
            }
            finally
            {
                await ReleaseSessionAsync(entry).AnyContext();
            }
        }
    }

    /// <summary>
    /// Removes every message from an emulator queue. Each pass scans the whole queue with paged peeks: active messages are
    /// received and completed, and scheduled messages are received once their <see cref="ServiceBusReceivedMessage.ScheduledEnqueueTime"/>
    /// passes (the emulator does not honor <c>CancelScheduledMessagesAsync</c>). Stops when the queue is empty, when the remaining
    /// messages are locked by another receiver, or when waiting for scheduled messages would exceed <see cref="MaxScheduledDrainWait"/> in total.
    /// </summary>
    private async Task TryAbandonUnsettledAsync(ServiceBusReceiver receiver, ServiceBusReceivedMessage message)
    {
        try
        {
            await receiver.AbandonMessageAsync(message, cancellationToken: DisposedCancellationToken).AnyContext();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Unable to abandon unsettled message {MessageId} in queue {QueueName}: {Message}", message.MessageId, _options.Name, ex.Message);
        }
    }

    private async Task DrainQueueAsync()
    {
        await EnsureQueueCreatedAsync().AnyContext();

        var drainDeadlineUtc = _timeProvider.GetUtcNow().Add(MaxScheduledDrainWait);
        int totalDrained = 0;
        while (!DisposedCancellationToken.IsCancellationRequested)
        {
            var utcNow = _timeProvider.GetUtcNow();
            var scan = await ScanQueueAsync(utcNow).AnyContext();
            if (scan.MessageCount is 0)
                break;

            int drained = RequiresSession
                ? await DrainSessionsAsync(scan.ReceivableCountBySession).AnyContext()
                : await DrainReceiverAsync(_queueReceiver!, scan.ReceivableCount).AnyContext();

            totalDrained += drained;
            if (drained > 0)
                continue;

            var nextScheduledUtc = scan.NextScheduledEnqueueTimeUtc;
            if (nextScheduledUtc is null || nextScheduledUtc > drainDeadlineUtc)
            {
                _logger.LogWarning("Unable to drain {Count} locked or scheduled messages from queue {QueueName}", scan.MessageCount, _options.Name);
                break;
            }

            await _timeProvider.Delay(nextScheduledUtc.Value - utcNow, DisposedCancellationToken).AnyContext();
        }

        if (totalDrained > 0)
            _logger.LogDebug("Drained {Count} messages from queue {QueueName}", totalDrained, _options.Name);
    }

    private async Task<DrainScan> ScanQueueAsync(DateTimeOffset utcNow)
    {
        var scan = new DrainScan();
        long fromSequenceNumber = 0;
        while (!DisposedCancellationToken.IsCancellationRequested)
        {
            var messages = await _queueReceiver!.PeekMessagesAsync(DrainBatchSize, fromSequenceNumber, DisposedCancellationToken).AnyContext();
            if (messages.Count is 0)
                break;

            foreach (var message in messages)
                scan.Add(message, utcNow);

            fromSequenceNumber = messages[^1].SequenceNumber + 1;
        }

        return scan;
    }

    private async Task<int> DrainSessionsAsync(IReadOnlyDictionary<string, int> receivableCountBySession)
    {
        int drained = 0;
        foreach (var (sessionId, receivableCount) in receivableCountBySession)
        {
            ServiceBusSessionReceiver sessionReceiver;
            try
            {
                sessionReceiver = await _client.Value.AcceptSessionAsync(_options.Name, sessionId, cancellationToken: DisposedCancellationToken).AnyContext();
            }
            catch (ServiceBusException ex) when (ex.Reason is ServiceBusFailureReason.SessionCannotBeLocked or ServiceBusFailureReason.ServiceTimeout)
            {
                continue;
            }

            try
            {
                drained += await DrainReceiverAsync(sessionReceiver, receivableCount).AnyContext();
            }
            finally
            {
                await sessionReceiver.DisposeAsync().AnyContext();
            }
        }

        return drained;
    }

    private async Task<int> DrainReceiverAsync(ServiceBusReceiver receiver, int receivableCount)
    {
        int drained = 0;
        while (drained < receivableCount && !DisposedCancellationToken.IsCancellationRequested)
        {
            int batchSize = Math.Min(receivableCount - drained, DrainBatchSize);
            var messages = await receiver.ReceiveMessagesAsync(batchSize, DrainReceiveWait, DisposedCancellationToken).AnyContext();
            if (messages.Count is 0)
                break;

            foreach (var message in messages)
            {
                await receiver.CompleteMessageAsync(message, DisposedCancellationToken).AnyContext();
                drained++;
            }
        }

        return drained;
    }

    /// <summary>
    /// Summary of one full peek pass over an emulator queue, built page by page so the drain doesn't hold every message.
    /// </summary>
    private sealed class DrainScan
    {
        private readonly Dictionary<string, int> _receivableCountBySession = new(StringComparer.Ordinal);

        public int MessageCount { get; private set; }
        public int ReceivableCount { get; private set; }
        public IReadOnlyDictionary<string, int> ReceivableCountBySession => _receivableCountBySession;
        public DateTimeOffset? NextScheduledEnqueueTimeUtc { get; private set; }

        public void Add(ServiceBusReceivedMessage message, DateTimeOffset utcNow)
        {
            MessageCount++;

            bool isScheduledLater = message.State is ServiceBusMessageState.Scheduled && message.ScheduledEnqueueTime > utcNow;
            if (isScheduledLater)
            {
                if (NextScheduledEnqueueTimeUtc is null || message.ScheduledEnqueueTime < NextScheduledEnqueueTimeUtc)
                    NextScheduledEnqueueTimeUtc = message.ScheduledEnqueueTime;

                return;
            }

            if (message.State is not (ServiceBusMessageState.Active or ServiceBusMessageState.Scheduled))
                return;

            ReceivableCount++;
            if (message.SessionId is not null)
                _receivableCountBySession[message.SessionId] = _receivableCountBySession.GetValueOrDefault(message.SessionId) + 1;
        }
    }

    private QueueStats CreateQueueStats(long queued = 0, long deadletter = 0)
    {
        return new QueueStats
        {
            Queued = queued,
            Working = 0,
            Deadletter = deadletter,
            Enqueued = _enqueuedCount,
            Dequeued = _dequeuedCount,
            Completed = _completedCount,
            Abandoned = _abandonedCount,
            Errors = _workerErrorCount,
            Timeouts = 0
        };
    }

    protected override async Task<QueueStats> GetQueueStatsImplAsync()
    {
        // Skip admin API calls when using the emulator - it doesn't support the management HTTP API
        if (!QueueIsCreated || _isEmulator)
            return CreateQueueStats();

        var properties = await _adminClient.Value.GetQueueRuntimePropertiesAsync(_options.Name).AnyContext();
        return CreateQueueStats(properties.Value.ActiveMessageCount, properties.Value.DeadLetterMessageCount);
    }

    protected override Task<IEnumerable<T>> GetDeadletterItemsImplAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    protected override async Task<string?> EnqueueImplAsync(T data, QueueEntryOptions options)
    {
        // The length is checked before Enqueuing handlers so an invalid value has no side effects; a missing group is only
        // rejected afterwards because a handler may still assign it.
        ValidateSessionIdLength(GetSessionId(options));

        if (!await OnEnqueuingAsync(data, options).AnyContext())
            return null;

        string? sessionId = GetSessionId(options);
        ValidateSessionIdLength(sessionId);
        if (sessionId is null && RequiresSession)
            throw new QueueException($"A GroupId is required when enqueuing to the session-enabled queue {_options.Name}.");

        Interlocked.Increment(ref _enqueuedCount);

        var message = new ServiceBusMessage(_serializer.SerializeToBytes(data));

        if (!String.IsNullOrEmpty(options.UniqueId))
            message.MessageId = options.UniqueId;

        if (!String.IsNullOrEmpty(options.CorrelationId))
            message.CorrelationId = options.CorrelationId;

        if (sessionId is not null)
            message.SessionId = sessionId;

        if (options.DeliveryDelay.HasValue && options.DeliveryDelay.Value > TimeSpan.Zero)
            message.ScheduledEnqueueTime = _timeProvider.GetUtcNow().Add(options.DeliveryDelay.Value);

        if (options.Properties is not null)
        {
            foreach (var property in options.Properties)
                message.ApplicationProperties[property.Key] = property.Value;
        }

        _logger.LogTrace("Enqueuing message to queue {QueueName}", _options.Name);

        await _queueSender!.SendMessageAsync(message).AnyContext();

        var entry = new QueueEntry<T>(message.MessageId, message.CorrelationId, data, this, _timeProvider.GetUtcNow().UtcDateTime, 0)
        {
            GroupId = String.IsNullOrEmpty(message.SessionId) ? null : message.SessionId
        };

        foreach (var property in message.ApplicationProperties)
        {
            if (property.Value?.ToString() is { } value)
                entry.Properties.Add(property.Key, value);
        }

        await OnEnqueuedAsync(entry).AnyContext();

        _logger.LogTrace("Enqueued message {MessageId} to queue {QueueName}", message.MessageId, _options.Name);
        return message.MessageId;
    }

    /// <summary>
    /// An explicit <see cref="AzureServiceBusQueueEntryOptions.SessionId"/> wins over <see cref="QueueEntryOptions.GroupId"/>.
    /// </summary>
    private static string? GetSessionId(QueueEntryOptions options)
    {
        if (options is AzureServiceBusQueueEntryOptions { SessionId: { Length: > 0 } sessionId })
            return sessionId;

        return String.IsNullOrEmpty(options.GroupId) ? null : options.GroupId;
    }

    private static void ValidateSessionIdLength(string? sessionId)
    {
        if (sessionId?.Length > MaxSessionIdLength)
            throw new ArgumentException($"{nameof(QueueEntryOptions.GroupId)} must be at most {MaxSessionIdLength} characters for Service Bus sessions.", nameof(QueueEntryOptions.GroupId));
    }

    protected override async Task<IQueueEntry<T>?> DequeueImplAsync(CancellationToken linkedCancellationToken)
    {
        // Calculate timeout based on cancellation state - like SQS pattern
        var timeout = linkedCancellationToken.IsCancellationRequested
            ? TimeSpan.FromMilliseconds(100)
            : _options.ReadQueueTimeout;

        _logger.LogTrace("Checking for message in queue {QueueName}... IsCancellationRequested={IsCancellationRequested} ReadQueueTimeout={ReadQueueTimeout}",
            _options.Name, linkedCancellationToken.IsCancellationRequested, timeout);

        await ReleaseExpiredEntriesAsync().AnyContext();

        ServiceBusReceivedMessage? message = null;
        ServiceBusSessionReceiver? sessionReceiver = null;

        // Initial receive attempt - try at least once even if cancellation is requested
        // This supports the pattern where TimeSpan.Zero is passed to mean "check once without waiting"
        try
        {
            (message, sessionReceiver) = await ReceiveMessageAsync(timeout, CancellationToken.None).AnyContext();
        }
        catch (OperationCanceledException)
        {
            _logger.LogTrace("Operation cancelled while receiving message");
            return null;
        }

        // Retry loop
        while (message is null && !linkedCancellationToken.IsCancellationRequested)
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
                (message, sessionReceiver) = await ReceiveMessageAsync(_options.ReadQueueTimeout, linkedCancellationToken).AnyContext();
            }
            catch (OperationCanceledException)
            {
                _logger.LogTrace("Operation cancelled while receiving message");
                return null;
            }
        }

        if (message is null)
        {
            _logger.LogTrace("No message received from queue {QueueName} IsCancellationRequested={IsCancellationRequested}",
                _options.Name, linkedCancellationToken.IsCancellationRequested);
            return null;
        }

        Interlocked.Increment(ref _dequeuedCount);
        _logger.LogTrace("Received message {MessageId} from queue {QueueName} IsCancellationRequested={IsCancellationRequested}",
            message.MessageId, _options.Name, linkedCancellationToken.IsCancellationRequested);

        T? data;
        Exception? deserializeException = null;
        try
        {
            data = _serializer.Deserialize<T>(message.Body.ToArray());
        }
        catch (Exception ex)
        {
            data = null;
            deserializeException = ex;
        }

        if (data is null)
        {
            _logger.LogWarning(deserializeException, "Error deserializing message {MessageId} (delivery {DeliveryCount}), abandoning for retry", message.MessageId, message.DeliveryCount);
            // Poison message: null! is intentional — deserialization failed, entry is immediately abandoned.
            var poisonEntry = new AzureServiceBusQueueEntry<T>(message, null!, this) { SessionReceiver = sessionReceiver };
            await AbandonAsync(poisonEntry).AnyContext();
            return null;
        }

        var entry = new AzureServiceBusQueueEntry<T>(message, data, this) { SessionReceiver = sessionReceiver };
        if (entry.Attempts > _options.Retries + 1)
        {
            try
            {
                await DeadLetterMessageAsync(entry).AnyContext();
            }
            finally
            {
                await ReleaseSessionAsync(entry).AnyContext();
            }

            Interlocked.Increment(ref _abandonedCount);
            return null;
        }

        try
        {
            await OnDequeuedAsync(entry).AnyContext();
        }
        catch
        {
            await ReleaseSessionAsync(entry).AnyContext();
            throw;
        }

        // A message redelivered after its lock expired has the same sequence number; release the stale entry's session.
        ServiceBusSessionReceiver? staleSessionReceiver = null;
        _unsettledEntries.AddOrUpdate(message.SequenceNumber, entry, (_, staleEntry) =>
        {
            staleSessionReceiver = staleEntry.SessionReceiver;
            return entry;
        });

        if (staleSessionReceiver is not null)
            await staleSessionReceiver.DisposeAsync().AnyContext();

        return entry;
    }

    /// <summary>
    /// Stops tracking entries whose lock already expired without being settled (for example a handler that never completed
    /// the entry). The broker has already made those messages available again, so tracking them would only leak memory,
    /// and on session queues an open receiver per entry until the queue is deleted or disposed.
    /// </summary>
    private async Task ReleaseExpiredEntriesAsync()
    {
        if (_unsettledEntries.IsEmpty)
            return;

        var releaseBeforeUtc = _timeProvider.GetUtcNow() - ExpiredLockGracePeriod;
        foreach (var tracked in _unsettledEntries)
        {
            var entry = tracked.Value;
            var lockedUntilUtc = entry.SessionReceiver?.SessionLockedUntil ?? entry.UnderlyingMessage.LockedUntil;
            if (lockedUntilUtc > releaseBeforeUtc || !_unsettledEntries.TryRemove(tracked))
                continue;

            _logger.LogDebug("Releasing unsettled message {MessageId} from queue {QueueName}; its lock expired at {LockedUntilUtc}", entry.UnderlyingMessage.MessageId, _options.Name, lockedUntilUtc);
            await ReleaseSessionAsync(entry).AnyContext();
        }
    }

    private async Task<(ServiceBusReceivedMessage? Message, ServiceBusSessionReceiver? SessionReceiver)> ReceiveMessageAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!RequiresSession)
            return (await _queueReceiver!.ReceiveMessageAsync(timeout, cancellationToken).AnyContext(), null);

        // Holding the session while its message is in flight keeps each group to one message at a time, in order.
        // Accepting and receiving share one deadline so a dequeue does not wait twice the timeout.
        if (timeout < MinSessionReceiveWait)
            timeout = MinSessionReceiveWait;

        long startedTimestamp = _timeProvider.GetTimestamp();
        var sessionReceiver = await AcceptNextSessionAsync(timeout, cancellationToken).AnyContext();
        if (sessionReceiver is null)
            return (null, null);

        try
        {
            var remaining = timeout - _timeProvider.GetElapsedTime(startedTimestamp);
            var receiveTimeout = remaining > MinSessionReceiveWait ? remaining : MinSessionReceiveWait;
            var message = await sessionReceiver.ReceiveMessageAsync(receiveTimeout, cancellationToken).AnyContext();
            if (message is not null)
                return (message, sessionReceiver);
        }
        catch
        {
            await sessionReceiver.DisposeAsync().AnyContext();
            throw;
        }

        await sessionReceiver.DisposeAsync().AnyContext();
        return (null, null);
    }

    private async Task<ServiceBusSessionReceiver?> AcceptNextSessionAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellationTokenSource.CancelAfter(timeout);

        try
        {
            return await _client.Value.AcceptNextSessionAsync(_options.Name, new ServiceBusSessionReceiverOptions
            {
                ReceiveMode = ServiceBusReceiveMode.PeekLock
            }, timeoutCancellationTokenSource.Token).AnyContext();
        }
        catch (ServiceBusException ex) when (ex.Reason is ServiceBusFailureReason.ServiceTimeout)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private ServiceBusReceiver GetReceiver(AzureServiceBusQueueEntry<T> entry)
    {
        return entry.SessionReceiver ?? _queueReceiver!;
    }

    private static async Task ReleaseSessionAsync(AzureServiceBusQueueEntry<T> entry)
    {
        if (entry.SessionReceiver is not null)
            await entry.SessionReceiver.DisposeAsync().AnyContext();
    }

    /// <summary>
    /// Stops tracking the entry for settlement on delete and closes its session, if any, which unlocks the group.
    /// </summary>
    private async Task UntrackAsync(AzureServiceBusQueueEntry<T> entry)
    {
        // Only remove this entry: a redelivery with the same sequence number may already be tracked in its place.
        _unsettledEntries.TryRemove(KeyValuePair.Create(entry.UnderlyingMessage.SequenceNumber, entry));
        await ReleaseSessionAsync(entry).AnyContext();
    }

    /// <summary>
    /// Releases the session of an entry that will not be settled. Closing the session unlocks its messages,
    /// so the entry is no longer tracked for settlement on delete.
    /// </summary>
    private async Task ReleaseUnsettledSessionAsync(AzureServiceBusQueueEntry<T> entry)
    {
        if (entry.SessionReceiver is not null)
            await UntrackAsync(entry).AnyContext();
    }

    public override async Task RenewLockAsync(IQueueEntry<T> queueEntry)
    {
        _logger.LogDebug("Queue {QueueName} renew lock item: {QueueEntryId}", _options.Name, queueEntry.Id);

        var entry = ToQueueEntry(queueEntry);

        if (entry.SessionReceiver is not null)
            await entry.SessionReceiver.RenewSessionLockAsync().AnyContext();
        else
            await _queueReceiver!.RenewMessageLockAsync(entry.UnderlyingMessage).AnyContext();

        await OnLockRenewedAsync(entry).AnyContext();
        _logger.LogTrace("Renew lock done: {QueueEntryId} MessageId={MessageId}", queueEntry.Id, entry.UnderlyingMessage.MessageId);
    }

    public override async Task CompleteAsync(IQueueEntry<T> queueEntry)
    {
        _logger.LogDebug("Queue {QueueName} complete item: {QueueEntryId}", _options.Name, queueEntry.Id);

        if (queueEntry.IsAbandoned || queueEntry.IsCompleted)
            throw new QueueException("Queue entry has already been completed or abandoned.");

        var entry = ToQueueEntry(queueEntry);

        // On failure the session stays open so the caller can still abandon the entry, which releases it.
        await GetReceiver(entry).CompleteMessageAsync(entry.UnderlyingMessage).AnyContext();
        await UntrackAsync(entry).AnyContext();

        Interlocked.Increment(ref _completedCount);
        queueEntry.MarkCompleted();
        await OnCompletedAsync(queueEntry).AnyContext();
        _logger.LogTrace("Complete done: {QueueEntryId} MessageId={MessageId}", queueEntry.Id, entry.UnderlyingMessage.MessageId);
    }

    public override async Task AbandonAsync(IQueueEntry<T> queueEntry)
    {
        _logger.LogDebug("Queue {QueueName} ({QueueId}) abandon item: {QueueEntryId}", _options.Name, QueueId, queueEntry.Id);

        if (queueEntry.IsAbandoned || queueEntry.IsCompleted)
            throw new QueueException("Queue entry has already been completed or abandoned.");

        var entry = ToQueueEntry(queueEntry);

        try
        {
            await AbandonMessageAsync(entry).AnyContext();
        }
        finally
        {
            await UntrackAsync(entry).AnyContext();
        }

        _logger.LogTrace("Abandoned queue entry: {QueueEntryId} MessageId={MessageId} Attempts={Attempts}",
            entry.Id, entry.UnderlyingMessage.MessageId, entry.Attempts);

        Interlocked.Increment(ref _abandonedCount);
        queueEntry.MarkAbandoned();

        await OnAbandonedAsync(queueEntry).AnyContext();
        _logger.LogTrace("Abandon complete: {QueueEntryId}", queueEntry.Id);
    }

    private async Task AbandonMessageAsync(AzureServiceBusQueueEntry<T> entry)
    {
        if (entry.Attempts > _options.Retries)
        {
            await DeadLetterMessageAsync(entry).AnyContext();
        }
        else
        {
            // Calculate retry delay based on attempt number
            var retryDelay = _options.RetryDelay(entry.Attempts);

            // Azure Service Bus doesn't support delayed retry on abandon natively, so a delayed retry completes the message
            // and schedules a copy. That copy is not used when:
            // - the queue uses sessions: the copy goes to the back of the session, so later messages in the group would be
            //   processed before the retry and per-group ordering would break; a plain abandon keeps it at the head;
            // - duplicate detection is enabled: the copy reuses the MessageId, so the broker would silently drop it.
            if (retryDelay > TimeSpan.Zero && !RequiresSession && (_queueRequiresDuplicateDetection ?? _options.RequiresDuplicateDetection) is not true)
            {
                _logger.LogTrace("Scheduling retry for queue entry: {QueueEntryId} MessageId={MessageId} RetryDelay={RetryDelay} Attempts={Attempts}",
                    entry.Id, entry.UnderlyingMessage.MessageId, retryDelay, entry.Attempts);

                // Create a new message with same content for scheduled retry
                var retryMessage = new ServiceBusMessage(entry.UnderlyingMessage.Body)
                {
                    MessageId = entry.UnderlyingMessage.MessageId,
                    CorrelationId = entry.UnderlyingMessage.CorrelationId,
                    ScheduledEnqueueTime = _timeProvider.GetUtcNow().Add(retryDelay),
                    SessionId = entry.UnderlyingMessage.SessionId,
                    Subject = entry.UnderlyingMessage.Subject,
                    ContentType = entry.UnderlyingMessage.ContentType,
                    ReplyTo = entry.UnderlyingMessage.ReplyTo,
                    To = entry.UnderlyingMessage.To
                };

                // Copy application properties (excluding SDK diagnostic properties)
                foreach (var prop in entry.UnderlyingMessage.ApplicationProperties)
                {
                    if (!ServiceBusMessageHelper.IsSdkDiagnosticProperty(prop.Key))
                        retryMessage.ApplicationProperties[prop.Key] = prop.Value;
                }

                // Store attempt count for next dequeue
                retryMessage.ApplicationProperties["_attempts"] = entry.Attempts;

                // Send the retry before completing the original: a failed send cannot lose the message, and a failed
                // complete redelivers it, which is the documented at-least-once Service Bus contract.
                await _queueSender!.SendMessageAsync(retryMessage).AnyContext();

                await GetReceiver(entry).CompleteMessageAsync(entry.UnderlyingMessage).AnyContext();
            }
            else
            {
                // No retry delay - just abandon for immediate retry
                await GetReceiver(entry).AbandonMessageAsync(entry.UnderlyingMessage).AnyContext();
            }
        }
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

                IQueueEntry<T>? entry = null;
                try
                {
                    entry = await DequeueImplAsync(linkedCancellationToken.Token).AnyContext();
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _workerErrorCount);
                    _logger.LogError(ex, "Error on Dequeue: {Message}", ex.Message);
                    var errorBackoff = _options.DequeueInterval > MinDequeueErrorBackoff ? _options.DequeueInterval : MinDequeueErrorBackoff;
                    try
                    {
                        await _timeProvider.Delay(errorBackoff, linkedCancellationToken.Token).AnyContext();
                    }
                    catch (OperationCanceledException) { }
                }

                if (entry is null)
                    continue;

                if (linkedCancellationToken.IsCancellationRequested)
                {
                    await ReleaseUnsettledSessionAsync(ToQueueEntry(entry)).AnyContext();
                    continue;
                }

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
                    {
                        try
                        {
                            await entry.AbandonAsync().AnyContext();
                        }
                        catch (Exception abandonEx)
                        {
                            _logger.LogError(abandonEx, "Error abandoning queue entry {QueueEntryId}: {Message}", entry.Id, abandonEx.Message);
                        }
                    }
                }
                finally
                {
                    if (!entry.IsAbandoned && !entry.IsCompleted && linkedCancellationToken.IsCancellationRequested)
                        await ReleaseUnsettledSessionAsync(ToQueueEntry(entry)).AnyContext();
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
        if (!SignalDispose())
        {
            _logger.LogTrace("Queue {QueueName} ({QueueId}) dispose was already called", _options.Name, QueueId);
            return;
        }

        if (_queueSender is not null)
        {
            _queueSender.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _queueSender = null;
        }

        if (_queueReceiver is not null)
        {
            _queueReceiver.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _queueReceiver = null;
        }

        if (_client.IsValueCreated)
        {
            _client.Value.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (!SignalDispose())
        {
            _logger.LogTrace("Queue {QueueName} ({QueueId}) async dispose was already called", _options.Name, QueueId);
            return;
        }

        if (_queueSender is not null)
        {
            await _queueSender.DisposeAsync().AnyContext();
            _queueSender = null;
        }

        if (_queueReceiver is not null)
        {
            await _queueReceiver.DisposeAsync().AnyContext();
            _queueReceiver = null;
        }

        if (_client.IsValueCreated)
        {
            await _client.Value.DisposeAsync().AnyContext();
        }

        base.Dispose();
    }

    private async Task DeadLetterMessageAsync(AzureServiceBusQueueEntry<T> entry)
    {
        _logger.LogInformation("Exceeded retry limit ({Attempts}/{Retries}), moving message {QueueEntryId} to dead letter", entry.Attempts, _options.Retries, entry.Id);

        await GetReceiver(entry).DeadLetterMessageAsync(entry.UnderlyingMessage, "MaxRetriesExceeded",
            $"Exceeded retry limit ({entry.Attempts} attempts, {_options.Retries} retries)").AnyContext();
    }

    private static AzureServiceBusQueueEntry<T> ToQueueEntry(IQueueEntry<T> entry)
    {
        if (entry is not AzureServiceBusQueueEntry<T> result)
            throw new ArgumentException($"Expected {nameof(AzureServiceBusQueueEntry<T>)} but received unknown queue entry type {entry.GetType()}");

        return result;
    }
}

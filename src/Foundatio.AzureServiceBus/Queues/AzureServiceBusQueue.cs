using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
using Foundatio.AzureServiceBus.Queues;
using Foundatio.Extensions;
using Foundatio.Messaging;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Management;
using Microsoft.Azure.ServiceBus.Core;
using Message = Microsoft.Azure.ServiceBus.Message;

namespace Foundatio.Queues {
    public class AzureServiceBusQueue<T> : QueueBase<T, AzureServiceBusQueueOptions<T>> where T : class {
        private readonly AsyncLock _lock = new AsyncLock();
        private readonly ManagementClient _managementClient;
        private MessageSender _queueSender;
        private MessageReceiver _queueReceiver;
        private long _enqueuedCount;
        private long _dequeuedCount;
        private long _completedCount;
        private long _abandonedCount;
        private long _workerErrorCount;

        public AzureServiceBusQueue(AzureServiceBusQueueOptions<T> options) : base(options) {
            if (String.IsNullOrEmpty(options.ConnectionString))
                throw new ArgumentException("ConnectionString is required.");

            if (options.Name.Length > 260)
                throw new ArgumentException("Queue name must be set and be less than 260 characters.");

            if (options.WorkItemTimeout > TimeSpan.FromMinutes(5))
                throw new ArgumentException("The maximum WorkItemTimeout value for is 5 minutes; the default value is 1 minute.");

            if (options.AutoDeleteOnIdle.HasValue && options.AutoDeleteOnIdle < TimeSpan.FromMinutes(5))
                throw new ArgumentException("The minimum AutoDeleteOnIdle duration is 5 minutes.");

            if (options.DuplicateDetectionHistoryTimeWindow.HasValue && (options.DuplicateDetectionHistoryTimeWindow < TimeSpan.FromSeconds(20.0) || options.DuplicateDetectionHistoryTimeWindow > TimeSpan.FromDays(7.0)))
                throw new ArgumentException("The minimum DuplicateDetectionHistoryTimeWindow duration is 20 seconds and maximum is 7 days.");

            if (options.UserMetadata != null && options.UserMetadata.Length > 260)
                throw new ArgumentException("Queue UserMetadata must be less than 1024 characters.");

            _managementClient = new ManagementClient(options.ConnectionString);
        }

        public AzureServiceBusQueue(Builder<AzureServiceBusQueueOptionsBuilder<T>, AzureServiceBusQueueOptions<T>> config)
            : this(config(new AzureServiceBusQueueOptionsBuilder<T>()).Build()) { }

        private bool QueueIsCreated => _queueReceiver != null && _queueSender != null;
        protected override async Task EnsureQueueCreatedAsync(CancellationToken cancellationToken = new CancellationToken()) {
            if (QueueIsCreated)
                return;

            using (await _lock.LockAsync().AnyContext()) {
                if (QueueIsCreated)
                    return;

                var sw = Stopwatch.StartNew();
                try {
                    await _managementClient.CreateQueueAsync(CreateQueueDescription()).AnyContext();
                } catch (MessagingEntityAlreadyExistsException) { }

                _queueSender = new MessageSender(_options.ConnectionString, _options.Name, _options.RetryPolicy);
                _queueReceiver = new MessageReceiver(_options.ConnectionString, _options.Name, ReceiveMode.PeekLock, _options.RetryPolicy);
                sw.Stop();
                _logger.LogTrace("Ensure queue exists took {0}ms.", sw.ElapsedMilliseconds);
            }
        }

        public override async Task DeleteQueueAsync() {
            if (await _managementClient.QueueExistsAsync(_options.Name).AnyContext())
                await _managementClient.DeleteQueueAsync(_options.Name).AnyContext();

            _queueSender = null;
            _queueReceiver = null;
            _enqueuedCount = 0;
            _dequeuedCount = 0;
            _completedCount = 0;
            _abandonedCount = 0;
            _workerErrorCount = 0;
        }

        protected override async Task<QueueStats> GetQueueStatsImplAsync() {
            var q = await _managementClient.GetQueueRuntimeInfoAsync(_options.Name).AnyContext();
            return new QueueStats {
                Queued = q.MessageCount,
                Working = 0,
                Deadletter = q.MessageCountDetails.DeadLetterMessageCount,
                Enqueued = _enqueuedCount,
                Dequeued = _dequeuedCount,
                Completed = _completedCount,
                Abandoned = _abandonedCount,
                Errors = _workerErrorCount,
                Timeouts = 0
            };
        }

        protected override Task<IEnumerable<T>> GetDeadletterItemsImplAsync(CancellationToken cancellationToken) {
            throw new NotImplementedException();
        }

        protected override async Task<string> EnqueueImplAsync(T data, QueueEntryOptions options) {
            if (!await OnEnqueuingAsync(data, options).AnyContext())
                return null;

            Interlocked.Increment(ref _enqueuedCount);
            var stream = new MemoryStream();
            _serializer.Serialize(data, stream);
            var brokeredMessage = new Message(stream.ToArray());
            brokeredMessage.MessageId = options.UniqueId;
            brokeredMessage.CorrelationId = options.CorrelationId;

            if (options is AzureServiceBusQueueEntryOptions asbOptions)
                brokeredMessage.SessionId = asbOptions.SessionId;

            if (options.DeliveryDelay.HasValue && options.DeliveryDelay.Value > TimeSpan.Zero)
                brokeredMessage.ScheduledEnqueueTimeUtc = DateTime.UtcNow.Add(options.DeliveryDelay.Value);

            foreach (var property in options.Properties)
                brokeredMessage.UserProperties[property.Key] = property.Value;
            
            await _queueSender.SendAsync(brokeredMessage).AnyContext();

            var entry = new QueueEntry<T>(brokeredMessage.MessageId, brokeredMessage.CorrelationId, data, this, SystemClock.UtcNow, 0);
            entry.SetLockToken(brokeredMessage);
            foreach (var property in brokeredMessage.UserProperties)
                entry.Properties.Add(property.Key, property.Value);
            await OnEnqueuedAsync(entry).AnyContext();

            return brokeredMessage.MessageId;
        }

        protected override void StartWorkingImpl(Func<IQueueEntry<T>, CancellationToken, Task> handler, bool autoComplete, CancellationToken cancellationToken) {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));
            
            _logger.LogTrace("WorkerLoop Start {QueueName}", _options.Name);
            _queueReceiver.RegisterMessageHandler(async (msg, cancellationToken1) => {
                _logger.LogTrace("WorkerLoop Signaled {QueueName}", _options.Name);
                var queueEntry = await HandleDequeueAsync(msg).AnyContext();

                try {
                    using (var linkedCancellationToken = GetLinkedDisposableCancellationTokenSource(cancellationToken)) {
                        await handler(queueEntry, linkedCancellationToken.Token).AnyContext();
                    }

                    if (autoComplete && !queueEntry.IsAbandoned && !queueEntry.IsCompleted)
                        await queueEntry.CompleteAsync().AnyContext();
                } catch (Exception ex) {
                    Interlocked.Increment(ref _workerErrorCount);
                    _logger.LogWarning(ex, "Error sending work item to worker: {0}", ex.Message);

                    if (!queueEntry.IsAbandoned && !queueEntry.IsCompleted)
                        await queueEntry.AbandonAsync().AnyContext();
                }
            }, new MessageHandlerOptions(LogMessageHandlerException));
        }

        private Task LogMessageHandlerException(ExceptionReceivedEventArgs e) {
            _logger.LogWarning("Exception: \"{0}\" {0}", e.Exception.Message, e.ExceptionReceivedContext.EntityPath);
            return Task.CompletedTask;
        }

        public override async Task<IQueueEntry<T>> DequeueAsync(TimeSpan? timeout = null) {
            if (!QueueIsCreated)
                await EnsureQueueCreatedAsync().AnyContext();

            var msg = await _queueReceiver.ReceiveAsync((timeout == null || timeout.Value.Ticks == 0) ? TimeSpan.FromSeconds(5) : timeout.Value).AnyContext();
            return await HandleDequeueAsync(msg).AnyContext();
        }

        protected override Task<IQueueEntry<T>> DequeueImplAsync(CancellationToken cancellationToken) {
            _logger.LogWarning("Azure Service Bus does not support CancellationTokens - use TimeSpan overload instead. Using default 30 second timeout.");
            return DequeueAsync();
        }

        public override async Task RenewLockAsync(IQueueEntry<T> entry) {
            _logger.LogDebug("Queue {0} renew lock item: {1}", _options.Name, entry.Id);
            await _queueReceiver.RenewLockAsync(entry.LockToken()).AnyContext();
            await OnLockRenewedAsync(entry).AnyContext();
            _logger.LogTrace("Renew lock done: {0}", entry.Id);
        }

        public override async Task CompleteAsync(IQueueEntry<T> entry) {
            _logger.LogDebug("Queue {0} complete item: {1}", _options.Name, entry.Id);
            if (entry.IsAbandoned || entry.IsCompleted)
                throw new InvalidOperationException("Queue entry has already been completed or abandoned.");

            await _queueReceiver.CompleteAsync(entry.LockToken()).AnyContext();
            Interlocked.Increment(ref _completedCount);
            entry.MarkCompleted();
            await OnCompletedAsync(entry).AnyContext();
            _logger.LogTrace("Complete done: {0}", entry.Id);
        }

        public override async Task AbandonAsync(IQueueEntry<T> entry) {
            _logger.LogDebug("Queue {QueueName}:{QueueId} abandon item: {EntryId}", _options.Name, QueueId, entry.Id);
            if (entry.IsAbandoned || entry.IsCompleted)
                throw new InvalidOperationException("Queue entry has already been completed or abandoned.");

            await _queueReceiver.AbandonAsync(entry.LockToken()).AnyContext();
            Interlocked.Increment(ref _abandonedCount);
            entry.MarkAbandoned();
            await OnAbandonedAsync(entry).AnyContext();
            _logger.LogTrace("Abandon complete: {EntryId}", entry.Id);
        }

        private async Task<IQueueEntry<T>> HandleDequeueAsync(Message brokeredMessage) {
            if (brokeredMessage == null)
                return null;

            var message = _serializer.Deserialize<T>(brokeredMessage.Body);
            Interlocked.Increment(ref _dequeuedCount);
            var entry = new QueueEntry<T>(brokeredMessage.MessageId, brokeredMessage.CorrelationId, message, this, brokeredMessage.SystemProperties.EnqueuedTimeUtc, brokeredMessage.SystemProperties.DeliveryCount);
            entry.SetLockToken(brokeredMessage);
            foreach (var property in brokeredMessage.UserProperties)
                entry.Properties.Add(property.Key, property.Value);
            await OnDequeuedAsync(entry).AnyContext();
            return entry;
        }

        private QueueDescription CreateQueueDescription() {
            var qd = new QueueDescription(_options.Name) {
                LockDuration = _options.WorkItemTimeout,
                MaxDeliveryCount = _options.Retries + 1
            };

            if (_options.AutoDeleteOnIdle.HasValue)
                qd.AutoDeleteOnIdle = _options.AutoDeleteOnIdle.Value;

            if (_options.DefaultMessageTimeToLive.HasValue)
                qd.DefaultMessageTimeToLive = _options.DefaultMessageTimeToLive.Value;

            if (_options.DuplicateDetectionHistoryTimeWindow.HasValue)
                qd.DuplicateDetectionHistoryTimeWindow = _options.DuplicateDetectionHistoryTimeWindow.Value;

            if (_options.EnableBatchedOperations.HasValue)
                qd.EnableBatchedOperations = _options.EnableBatchedOperations.Value;

            if (_options.EnableDeadLetteringOnMessageExpiration.HasValue)
                qd.EnableDeadLetteringOnMessageExpiration = _options.EnableDeadLetteringOnMessageExpiration.Value;

           

            if (_options.EnablePartitioning.HasValue)
                qd.EnablePartitioning = _options.EnablePartitioning.Value;

            if (!String.IsNullOrEmpty(_options.ForwardDeadLetteredMessagesTo))
                qd.ForwardDeadLetteredMessagesTo = _options.ForwardDeadLetteredMessagesTo;

            if (!String.IsNullOrEmpty(_options.ForwardTo))
                qd.ForwardTo = _options.ForwardTo;

           
            if (_options.MaxSizeInMegabytes.HasValue)
                qd.MaxSizeInMB = _options.MaxSizeInMegabytes.Value;

            if (_options.RequiresDuplicateDetection.HasValue)
                qd.RequiresDuplicateDetection = _options.RequiresDuplicateDetection.Value;

            if (_options.RequiresSession.HasValue)
                qd.RequiresSession = _options.RequiresSession.Value;

            if (_options.Status.HasValue)
                qd.Status = _options.Status.Value;

            if (!String.IsNullOrEmpty(_options.UserMetadata))
                qd.UserMetadata = _options.UserMetadata;
            
            return qd;
        }

        public override void Dispose() {
            base.Dispose();
            CloseSender();
            CloseReceiver();
            _managementClient.CloseAsync();
        }

        private void CloseSender() {
            if (_queueSender == null)
                return;

            using (_lock.Lock()) {
                if (_queueSender == null)
                    return;

                _queueSender?.CloseAsync();
                _queueSender = null;
            }
        }

        private void CloseReceiver() {
            if (_queueReceiver == null)
                return;

            using (_lock.Lock()) {
                if (_queueReceiver == null)
                    return;

                _queueReceiver?.CloseAsync();
                _queueReceiver = null;
            }
        }
    }
}
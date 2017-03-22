using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.ServiceBus;
using Microsoft.ServiceBus.Messaging;
using Nito.AsyncEx;

namespace Foundatio.Queues {
    public class AzureServiceBusQueue<T> : QueueBase<T, AzureServiceBusQueueOptions<T>> where T : class {
        private readonly AsyncLock _lock = new AsyncLock();
        private readonly NamespaceManager _namespaceManager;
        private QueueClient _queueClient;
        private long _enqueuedCount;
        private long _dequeuedCount;
        private long _completedCount;
        private long _abandonedCount;
        private long _workerErrorCount;

        [Obsolete("Use the options overload")]
        public AzureServiceBusQueue(string connectionString, string queueName = null, int retries = 2, TimeSpan? workItemTimeout = null, RetryPolicy retryPolicy = null, ISerializer serializer = null, IEnumerable<IQueueBehavior<T>> behaviors = null, ILoggerFactory loggerFactory = null, TimeSpan? autoDeleteOnIdle = null, TimeSpan? defaultMessageTimeToLive = null)
            : this(new AzureServiceBusQueueOptions<T>() {
                ConnectionString = connectionString,
                Name = queueName,
                Retries = retries,
                RetryPolicy = retryPolicy,
                AutoDeleteOnIdle = autoDeleteOnIdle.GetValueOrDefault(TimeSpan.MaxValue),
                DefaultMessageTimeToLive = defaultMessageTimeToLive.GetValueOrDefault(TimeSpan.MaxValue),
                WorkItemTimeout = workItemTimeout.GetValueOrDefault(TimeSpan.FromMinutes(5)),
                Behaviors = behaviors,
                Serializer = serializer,
                LoggerFactory = loggerFactory
            }) { }

        public AzureServiceBusQueue(AzureServiceBusQueueOptions<T> options) : base(options) {
            if (String.IsNullOrEmpty(options.ConnectionString))
                throw new ArgumentException("ConnectionString is required.");

            if (options.Name.Length > 260)
                throw new ArgumentException("Queue name must and less than 260 characters");

            if (options.WorkItemTimeout > TimeSpan.FromMinutes(5))
                throw new ArgumentException("The maximum work item timeout value for is 5 minutes; the default value is 1 minute.");

            _namespaceManager = NamespaceManager.CreateFromConnectionString(options.ConnectionString);
        }

        protected override async Task EnsureQueueCreatedAsync(CancellationToken cancellationToken = new CancellationToken()) {
            if (_queueClient != null)
                return;

            using (await _lock.LockAsync(cancellationToken).AnyContext()) {
                if (_queueClient != null)
                    return;

                bool shouldUpdateQueueSettings = false;
                QueueDescription queueDescription;
                if (!await _namespaceManager.QueueExistsAsync(_options.Name).AnyContext()) {
                    try {
                        queueDescription = await _namespaceManager.CreateQueueAsync(CreateQueueDescription()).AnyContext();
                    } catch (MessagingException) {
                        shouldUpdateQueueSettings = true;
                        queueDescription = await _namespaceManager.GetQueueAsync(_options.Name).AnyContext();
                    }
                } else {
                    shouldUpdateQueueSettings = true;
                    queueDescription = await _namespaceManager.GetQueueAsync(_options.Name).AnyContext();
                }

                if (shouldUpdateQueueSettings) {
                    bool changes = UpdateQueueDescription(queueDescription);
                    if (changes)
                        await _namespaceManager.UpdateQueueAsync(queueDescription).AnyContext();
                }

                _queueClient = QueueClient.CreateFromConnectionString(_options.ConnectionString, queueDescription.Path);
                if (_options != null)
                    _queueClient.RetryPolicy = _options.RetryPolicy;
            }
        }

        public override async Task DeleteQueueAsync() {
            if (await _namespaceManager.QueueExistsAsync(_options.Name).AnyContext())
                await _namespaceManager.DeleteQueueAsync(_options.Name).AnyContext();

            _queueClient = null;
            _enqueuedCount = 0;
            _dequeuedCount = 0;
            _completedCount = 0;
            _abandonedCount = 0;
            _workerErrorCount = 0;
        }

        protected override async Task<QueueStats> GetQueueStatsImplAsync() {
            var q = await _namespaceManager.GetQueueAsync(_options.Name).AnyContext();
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

        protected override async Task<string> EnqueueImplAsync(T data) {
            if (!await OnEnqueuingAsync(data).AnyContext())
                return null;

            Interlocked.Increment(ref _enqueuedCount);
            var message = new BrokeredMessage(data);
            await _queueClient.SendAsync(message).AnyContext();

            var entry = new QueueEntry<T>(message.MessageId, data, this, SystemClock.UtcNow, 0);
            await OnEnqueuedAsync(entry).AnyContext();

            return message.MessageId;
        }

        protected override void StartWorkingImpl(Func<IQueueEntry<T>, CancellationToken, Task> handler, bool autoComplete, CancellationToken cancellationToken) {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            var linkedCancellationToken = GetLinkedDisposableCanncellationToken(cancellationToken);

            // TODO: How do you unsubscribe from this or bail out on queue disposed?
            _logger.Trace("WorkerLoop Start {_options.Name}", _options.Name);
            _queueClient.OnMessageAsync(async msg => {
                _logger.Trace("WorkerLoop Signaled {_options.Name}", _options.Name);
                var queueEntry = await HandleDequeueAsync(msg).AnyContext();

                try {
                    await handler(queueEntry, linkedCancellationToken).AnyContext();
                    if (autoComplete && !queueEntry.IsAbandoned && !queueEntry.IsCompleted)
                        await queueEntry.CompleteAsync().AnyContext();
                } catch (Exception ex) {
                    Interlocked.Increment(ref _workerErrorCount);
                    _logger.Error(ex, "Error sending work item to worker: {0}", ex.Message);

                    if (!queueEntry.IsAbandoned && !queueEntry.IsCompleted)
                        await queueEntry.AbandonAsync().AnyContext();
                }
            }, new OnMessageOptions { AutoComplete = false });
        }

        public override async Task<IQueueEntry<T>> DequeueAsync(TimeSpan? timeout = null) {
            await EnsureQueueCreatedAsync().AnyContext();

            using (var msg = await _queueClient.ReceiveAsync(timeout ?? TimeSpan.FromSeconds(30)).AnyContext()) {
                return await HandleDequeueAsync(msg).AnyContext();
            }
        }

        protected override Task<IQueueEntry<T>> DequeueImplAsync(CancellationToken cancellationToken) {
            _logger.Warn("Azure Service Bus does not support CancellationTokens - use TimeSpan overload instead. Using default 30 second timeout.");
            return DequeueAsync();
        }

        public override async Task RenewLockAsync(IQueueEntry<T> entry) {
            await EnsureQueueCreatedAsync().AnyContext(); // Azure SB needs to call this as it populates the _queueClient field

            await _queueClient.RenewMessageLockAsync(new Guid(entry.Id)).AnyContext();
            await OnLockRenewedAsync(entry).AnyContext();
        }

        public override async Task CompleteAsync(IQueueEntry<T> entry) {
            if (entry.IsAbandoned || entry.IsCompleted)
                throw new InvalidOperationException("Queue entry has already been completed or abandoned.");

            await EnsureQueueCreatedAsync().AnyContext(); // Azure SB needs to call this as it populates the _queueClient field

            await _queueClient.CompleteAsync(new Guid(entry.Id)).AnyContext();
            Interlocked.Increment(ref _completedCount);
            entry.MarkCompleted();
            await OnCompletedAsync(entry).AnyContext();
        }

        public override async Task AbandonAsync(IQueueEntry<T> entry) {
            if (entry.IsAbandoned || entry.IsCompleted)
                throw new InvalidOperationException("Queue entry has already been completed or abandoned.");

            await EnsureQueueCreatedAsync().AnyContext(); // Azure SB needs to call this as it populates the _queueClient field

            await _queueClient.AbandonAsync(new Guid(entry.Id)).AnyContext();
            Interlocked.Increment(ref _abandonedCount);
            entry.MarkAbandoned();
            await OnAbandonedAsync(entry).AnyContext();
        }

        public override void Dispose() {
            base.Dispose();
            _queueClient?.Close();
        }

        private async Task<IQueueEntry<T>> HandleDequeueAsync(BrokeredMessage msg) {
            if (msg == null)
                return null;

            var data = msg.GetBody<T>();
            Interlocked.Increment(ref _dequeuedCount);
            var entry = new QueueEntry<T>(msg.LockToken.ToString(), data, this, msg.EnqueuedTimeUtc, msg.DeliveryCount);
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

            if (_options.EnableExpress.HasValue)
                qd.EnableExpress = _options.EnableExpress.Value;

            if (_options.EnablePartitioning.HasValue)
                qd.EnablePartitioning = _options.EnablePartitioning.Value;

            if (!String.IsNullOrEmpty(_options.ForwardDeadLetteredMessagesTo))
                qd.ForwardDeadLetteredMessagesTo = _options.ForwardDeadLetteredMessagesTo;

            if (!String.IsNullOrEmpty(_options.ForwardTo))
                qd.ForwardTo = _options.ForwardTo;

            if (_options.IsAnonymousAccessible.HasValue)
                qd.IsAnonymousAccessible = _options.IsAnonymousAccessible.Value;

            if (_options.MaxSizeInMegabytes.HasValue)
                qd.MaxSizeInMegabytes = _options.MaxSizeInMegabytes.Value;

            if (_options.RequiresDuplicateDetection.HasValue)
                qd.RequiresDuplicateDetection = _options.RequiresDuplicateDetection.Value;

            if (_options.RequiresSession.HasValue)
                qd.RequiresSession = _options.RequiresSession.Value;

            if (_options.Status.HasValue)
                qd.Status = _options.Status.Value;

            if (_options.SupportOrdering.HasValue)
                qd.SupportOrdering = _options.SupportOrdering.Value;

            if (!String.IsNullOrEmpty(_options.UserMetadata))
                qd.UserMetadata = _options.UserMetadata;

            return qd;
        }

        private bool UpdateQueueDescription(QueueDescription qd) {
            bool changes = false;
            if (_options.AutoDeleteOnIdle.HasValue && qd.AutoDeleteOnIdle != _options.AutoDeleteOnIdle.Value) {
                qd.AutoDeleteOnIdle = _options.AutoDeleteOnIdle.Value;
                changes = true;
            }

            if (_options.DefaultMessageTimeToLive.HasValue && qd.DefaultMessageTimeToLive != _options.DefaultMessageTimeToLive.Value) {
                qd.DefaultMessageTimeToLive = _options.DefaultMessageTimeToLive.Value;
                changes = true;
            }

            if (_options.DuplicateDetectionHistoryTimeWindow.HasValue && qd.DuplicateDetectionHistoryTimeWindow != _options.DuplicateDetectionHistoryTimeWindow.Value) {
                qd.DuplicateDetectionHistoryTimeWindow = _options.DuplicateDetectionHistoryTimeWindow.Value;
                changes = true;
            }

            if (_options.EnableBatchedOperations.HasValue && qd.EnableBatchedOperations != _options.EnableBatchedOperations.Value) {
                qd.EnableBatchedOperations = _options.EnableBatchedOperations.Value;
                changes = true;
            }

            if (_options.EnableDeadLetteringOnMessageExpiration.HasValue && qd.EnableDeadLetteringOnMessageExpiration != _options.EnableDeadLetteringOnMessageExpiration.Value) {
                qd.EnableDeadLetteringOnMessageExpiration = _options.EnableDeadLetteringOnMessageExpiration.Value;
                changes = true;
            }

            if (qd.ForwardDeadLetteredMessagesTo != _options.ForwardDeadLetteredMessagesTo) {
                qd.ForwardDeadLetteredMessagesTo = _options.ForwardDeadLetteredMessagesTo;
                changes = true;
            }

            if (qd.ForwardTo != _options.ForwardTo) {
                qd.ForwardTo = _options.ForwardTo;
                changes = true;
            }

            if (_options.IsAnonymousAccessible.HasValue && qd.IsAnonymousAccessible != _options.IsAnonymousAccessible.Value) {
                qd.IsAnonymousAccessible = _options.IsAnonymousAccessible.Value;
                changes = true;
            }

            if (qd.LockDuration != _options.WorkItemTimeout) {
                qd.LockDuration = _options.WorkItemTimeout;
                changes = true;
            }

            if (qd.MaxDeliveryCount != _options.Retries + 1) {
                qd.MaxDeliveryCount = _options.Retries + 1;
                changes = true;
            }

            if (_options.Status.HasValue && qd.Status != _options.Status.Value) {
                qd.Status = _options.Status.Value;
                changes = true;
            }

            if (_options.SupportOrdering.HasValue && qd.SupportOrdering != _options.SupportOrdering.Value) {
                qd.SupportOrdering = _options.SupportOrdering.Value;
                changes = true;
            }

            if (qd.UserMetadata != _options.UserMetadata) {
                qd.UserMetadata = _options.UserMetadata;
                changes = true;
            }

            return changes;
        }
    }
}
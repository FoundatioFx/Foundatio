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
    public class AzureServiceBusQueue<T> : QueueBase<T> where T : class {
        private readonly string _connectionString;
        private readonly NamespaceManager _namespaceManager;
        private QueueClient _queueClient;
        private long _enqueuedCount;
        private long _dequeuedCount;
        private long _completedCount;
        private long _abandonedCount;
        private long _workerErrorCount;
        private readonly int _retries;
        private readonly RetryPolicy _retryPolicy;
        private readonly TimeSpan _workItemTimeout = TimeSpan.FromMinutes(5);
        private readonly TimeSpan _autoDeleteOnIdle = TimeSpan.MaxValue;
        private readonly TimeSpan _defaultMessageTimeToLive = TimeSpan.MaxValue;
        private readonly AsyncLock _lock = new AsyncLock();

        public AzureServiceBusQueue(string connectionString, string queueName = null, int retries = 2, TimeSpan? workItemTimeout = null, RetryPolicy retryPolicy = null, ISerializer serializer = null, IEnumerable<IQueueBehavior<T>> behaviors = null, ILoggerFactory loggerFactory = null, TimeSpan? autoDeleteOnIdle = null, TimeSpan? defaultMessageTimeToLive = null) : base(serializer, behaviors, loggerFactory) {
            _connectionString = connectionString;
            if (!String.IsNullOrEmpty(queueName))
                _queueName = queueName;
            _namespaceManager = NamespaceManager.CreateFromConnectionString(connectionString);
            _retries = retries;
            _retryPolicy = retryPolicy;

            if (workItemTimeout.HasValue && workItemTimeout.Value < TimeSpan.FromMinutes(5)) {
                _workItemTimeout = workItemTimeout.Value;
            }

            if (autoDeleteOnIdle.HasValue && autoDeleteOnIdle.Value >= TimeSpan.FromMinutes(5)) {
                _autoDeleteOnIdle = autoDeleteOnIdle.Value;
            }

            if (defaultMessageTimeToLive.HasValue && defaultMessageTimeToLive.Value > TimeSpan.Zero) {
                _defaultMessageTimeToLive = defaultMessageTimeToLive.Value;
            }
        }
            
        protected override async Task EnsureQueueCreatedAsync(CancellationToken cancellationToken = new CancellationToken()) {
            if (_queueClient != null) {
                return;
            }

            using (await _lock.LockAsync(cancellationToken)) {
                if (_queueClient != null) {
                    return;
                }

                QueueDescription queueDescription;

                if (!await _namespaceManager.QueueExistsAsync(_queueName).AnyContext()) {
                    try {
                        queueDescription = await _namespaceManager.CreateQueueAsync(new QueueDescription(_queueName) {
                            MaxDeliveryCount = _retries + 1,
                            LockDuration = _workItemTimeout,
                            AutoDeleteOnIdle = _autoDeleteOnIdle,
                            DefaultMessageTimeToLive = _defaultMessageTimeToLive
                        }).AnyContext();
                    }
                    catch (MessagingException) {
                        queueDescription = await _namespaceManager.GetQueueAsync(_queueName).AnyContext();
                    }
                }
                else {
                    queueDescription = await _namespaceManager.GetQueueAsync(_queueName).AnyContext();

                    bool changes = false;

                    int newMaxDeliveryCount = _retries + 1;
                    if (queueDescription.MaxDeliveryCount != newMaxDeliveryCount) {
                        queueDescription.MaxDeliveryCount = newMaxDeliveryCount;
                        changes = true;
                    }

                    if (queueDescription.LockDuration != _workItemTimeout) {
                        queueDescription.LockDuration = _workItemTimeout;
                        changes = true;
                    }

                    if (queueDescription.AutoDeleteOnIdle != _autoDeleteOnIdle) {
                        queueDescription.AutoDeleteOnIdle = _autoDeleteOnIdle;
                        changes = true;
                    }

                    if (queueDescription.DefaultMessageTimeToLive != _defaultMessageTimeToLive) {
                        queueDescription.DefaultMessageTimeToLive = _defaultMessageTimeToLive;
                        changes = true;
                    }

                    if (changes) {
                        await _namespaceManager.UpdateQueueAsync(queueDescription).AnyContext();
                    }
                }

                _queueClient = QueueClient.CreateFromConnectionString(_connectionString, queueDescription.Path);

                if (_retryPolicy != null) {
                    _queueClient.RetryPolicy = _retryPolicy;
                }
            }
        }

        public override async Task DeleteQueueAsync() {
            if (await _namespaceManager.QueueExistsAsync(_queueName).AnyContext()) {
                await _namespaceManager.DeleteQueueAsync(_queueName).AnyContext();
            }

            _queueClient = null;

            _enqueuedCount = 0;
            _dequeuedCount = 0;
            _completedCount = 0;
            _abandonedCount = 0;
            _workerErrorCount = 0;
        }

        protected override async Task<QueueStats> GetQueueStatsImplAsync() {
            var q = await _namespaceManager.GetQueueAsync(_queueName).AnyContext();
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
            
            _queueClient.OnMessageAsync(async msg => {
                var queueEntry = await HandleDequeueAsync(msg).AnyContext();

                try {
                    await handler(queueEntry, cancellationToken).AnyContext();
                    if (autoComplete && !queueEntry.IsAbandoned && !queueEntry.IsCompleted)
                        await queueEntry.CompleteAsync().AnyContext();
                } catch (Exception ex) {
                    Interlocked.Increment(ref _workerErrorCount);
                    _logger.Error(ex, "Error sending work item to worker: {0}", ex.Message);

                    if (!queueEntry.IsAbandoned && !queueEntry.IsCompleted)
                        await queueEntry.AbandonAsync().AnyContext();
                }
            }, new OnMessageOptions {
                AutoComplete = false
            });
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
            await EnsureQueueCreatedAsync().AnyContext(); // Azure SB needs to call this as it populates the _queueClient field

            Interlocked.Increment(ref _completedCount);
            await _queueClient.CompleteAsync(new Guid(entry.Id)).AnyContext();
            await OnCompletedAsync(entry).AnyContext();
        }
        
        public override async Task AbandonAsync(IQueueEntry<T> entry) {
            await EnsureQueueCreatedAsync().AnyContext(); // Azure SB needs to call this as it populates the _queueClient field

            Interlocked.Increment(ref _abandonedCount);
            await _queueClient.AbandonAsync(new Guid(entry.Id)).AnyContext();
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
    }
}

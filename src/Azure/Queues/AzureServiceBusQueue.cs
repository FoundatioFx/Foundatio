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

namespace Foundatio.Queues {
    public class AzureServiceBusQueue<T> : QueueBase<T> where T : class {
        private readonly string _queueName;
        private readonly NamespaceManager _namespaceManager;
        private readonly QueueClient _queueClient;
        private QueueDescription _queueDescription;
        private long _enqueuedCount;
        private long _dequeuedCount;
        private long _completedCount;
        private long _abandonedCount;
        private long _workerErrorCount;
        private readonly int _retries;
        private readonly TimeSpan _workItemTimeout = TimeSpan.FromMinutes(5);

        public AzureServiceBusQueue(string connectionString, string queueName = null, int retries = 2, TimeSpan? workItemTimeout = null, bool shouldRecreate = false, RetryPolicy retryPolicy = null, ISerializer serializer = null, IEnumerable<IQueueBehavior<T>> behaviors = null) : base(serializer, behaviors) {
            _queueName = queueName ?? typeof(T).Name;
            _namespaceManager = NamespaceManager.CreateFromConnectionString(connectionString);
            _retries = retries;
            if (workItemTimeout.HasValue && workItemTimeout.Value < TimeSpan.FromMinutes(5))
                _workItemTimeout = workItemTimeout.Value;

            if (_namespaceManager.QueueExists(_queueName) && shouldRecreate)
                _namespaceManager.DeleteQueue(_queueName);

            if (!_namespaceManager.QueueExists(_queueName)) {
                _queueDescription = new QueueDescription(_queueName) {
                    MaxDeliveryCount = retries + 1,
                    LockDuration = _workItemTimeout
                };
                _namespaceManager.CreateQueue(_queueDescription);
            } else {
                _queueDescription = _namespaceManager.GetQueue(_queueName);
                _queueDescription.MaxDeliveryCount = retries + 1;
                _queueDescription.LockDuration = _workItemTimeout;
            }

            _queueClient = QueueClient.CreateFromConnectionString(connectionString, _queueDescription.Path);
            if (retryPolicy != null)
                _queueClient.RetryPolicy = retryPolicy;
        }

        public override async Task DeleteQueueAsync() {
            if (await _namespaceManager.QueueExistsAsync(_queueName).AnyContext())
                await _namespaceManager.DeleteQueueAsync(_queueName).AnyContext();

            _queueDescription = new QueueDescription(_queueName) {
                MaxDeliveryCount = _retries + 1,
                LockDuration = _workItemTimeout
            };

            await _namespaceManager.CreateQueueAsync(_queueDescription).AnyContext();

            _enqueuedCount = 0;
            _dequeuedCount = 0;
            _completedCount = 0;
            _abandonedCount = 0;
            _workerErrorCount = 0;
        }

        public override async Task<QueueStats> GetQueueStatsAsync() {
            var q = await _namespaceManager.GetQueueAsync(_queueName).AnyContext();
            return new QueueStats {
                Queued = q.MessageCount,
                Working = -1,
                Deadletter = q.MessageCountDetails.DeadLetterMessageCount,
                Enqueued = _enqueuedCount,
                Dequeued = _dequeuedCount,
                Completed = _completedCount,
                Abandoned = _abandonedCount,
                Errors = _workerErrorCount,
                Timeouts = 0
            };
        }

        public override Task<IEnumerable<T>> GetDeadletterItemsAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            throw new NotImplementedException();
        }
        
        public override async Task<string> EnqueueAsync(T data) {
            if (!await OnEnqueuingAsync(data).AnyContext())
                return null;

            Interlocked.Increment(ref _enqueuedCount);
            var message = new BrokeredMessage(data);
            await _queueClient.SendAsync(message).AnyContext();
            
            var entry = new QueueEntry<T>(message.MessageId, data, this, DateTime.UtcNow, 0);
            await OnEnqueuedAsync(entry).AnyContext();

            return message.MessageId;
        }
        
        public override void StartWorking(Func<IQueueEntry<T>, CancellationToken, Task> handler, bool autoComplete = false, CancellationToken cancellationToken = default(CancellationToken)) {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));
            
            // TODO: use the cancellation token.

            _queueClient.OnMessageAsync(async message => {
                Interlocked.Increment(ref _dequeuedCount);
                var data = message.GetBody<T>();

                var workItem = new QueueEntry<T>(message.LockToken.ToString(), data, this, message.EnqueuedTimeUtc, message.DeliveryCount);
                try {
                    await handler(workItem, cancellationToken).AnyContext();
                    if (autoComplete)
                        await workItem.CompleteAsync().AnyContext();
                } catch (Exception ex) {
                    Interlocked.Increment(ref _workerErrorCount);
                    Logger.Error().Exception(ex).Message("Error sending work item to worker: {0}", ex.Message).Write();
                    await workItem.AbandonAsync().AnyContext();
                }
            });
        }

        public override async Task<IQueueEntry<T>> DequeueAsync(TimeSpan? timeout = null) {
            using (var msg = await _queueClient.ReceiveAsync(timeout ?? TimeSpan.FromSeconds(30)).AnyContext()) {
                if (msg == null)
                    return null;

                var data = msg.GetBody<T>();
                Interlocked.Increment(ref _dequeuedCount);
                var entry = new QueueEntry<T>(msg.LockToken.ToString(), data, this, msg.EnqueuedTimeUtc, msg.DeliveryCount);
                await OnDequeuedAsync(entry).AnyContext();
                return entry;
            }
        }

        public override Task<IQueueEntry<T>> DequeueAsync(CancellationToken cancellationToken) {
            Logger.Warn().Message("Azure Service Bus does not support CancellationTokens - use TimeSpan overload instead. Using default 30 second timeout.").Write();

            return DequeueAsync(null);
        }

        public override Task RenewLockAsync(IQueueEntry<T> queueEntry) {
            return TaskHelper.Completed();
        }

        public override async Task CompleteAsync(IQueueEntry<T> entry) {
            Interlocked.Increment(ref _completedCount);
            await _queueClient.CompleteAsync(new Guid(entry.Id)).AnyContext();
            await OnCompletedAsync(entry).AnyContext();
        }
        
        public override async Task AbandonAsync(IQueueEntry<T> entry) {
            Interlocked.Increment(ref _abandonedCount);
            await _queueClient.AbandonAsync(new Guid(entry.Id)).AnyContext();
            await OnAbandonedAsync(entry).AnyContext();
        }
        
        public override void Dispose() {
            _queueClient.Close();
            base.Dispose();
        }
    }
}

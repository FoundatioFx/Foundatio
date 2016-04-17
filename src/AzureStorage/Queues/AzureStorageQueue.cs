using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Serializer;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Queue;
using Microsoft.WindowsAzure.Storage.RetryPolicies;
using Nito.AsyncEx;
using Nito.AsyncEx.Synchronous;

namespace Foundatio.Queues {
    public class AzureStorageQueue<T> : QueueBase<T> where T : class {
        private readonly string _queueName;
        private readonly CloudQueue _queueReference;
        private readonly CloudQueue _deadletterQueueReference;
        private long _enqueuedCount;
        private long _dequeuedCount;
        private long _completedCount;
        private long _abandonedCount;
        private long _workerErrorCount;
        private readonly CancellationTokenSource _queueDisposedCancellationTokenSource;
        private readonly int _retries;
        private readonly TimeSpan _workItemTimeout = TimeSpan.FromMinutes(5);
        private readonly TimeSpan _dequeueInterval = TimeSpan.FromSeconds(1);
        private readonly AsyncLock _lock = new AsyncLock();
        private bool _queueCreated;

        public AzureStorageQueue(string connectionString, string queueName = null, int retries = 2, TimeSpan? workItemTimeout = null, TimeSpan? dequeueInterval = null,
            IRetryPolicy retryPolicy = null, ISerializer serializer = null, IEnumerable<IQueueBehavior<T>> behaviors = null, ILoggerFactory loggerFactory = null)
            : base(serializer, behaviors, loggerFactory) {
            var account = CloudStorageAccount.Parse(connectionString);
            var client = account.CreateCloudQueueClient();

            _queueName = queueName;
            _queueReference = client.GetQueueReference(queueName);
            _deadletterQueueReference = client.GetQueueReference($"{queueName}-deadletter");
            
            _queueDisposedCancellationTokenSource = new CancellationTokenSource();

            _retries = retries;

            if (workItemTimeout.HasValue)
                _workItemTimeout = workItemTimeout.Value;
            if (dequeueInterval.HasValue)
                _dequeueInterval = dequeueInterval.Value;
            if (retryPolicy != null) 
                client.DefaultRequestOptions.RetryPolicy = retryPolicy;
        }

        protected override async Task EnsureQueueCreatedAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            if (_queueCreated) {
                return;
            }

            using (await _lock.LockAsync(cancellationToken)) {
                if (_queueCreated) {
                    return;
                }
                
                await _queueReference.CreateIfNotExistsAsync(cancellationToken).AnyContext();
                await _deadletterQueueReference.CreateIfNotExistsAsync(cancellationToken).AnyContext();

                _queueCreated = true;
            }
        }

        protected override async Task<string> EnqueueImplAsync(T data) {
            if (!await OnEnqueuingAsync(data).AnyContext())
                return null;

            Interlocked.Increment(ref _enqueuedCount);
            var message = new CloudQueueMessage(await _serializer.SerializeAsync(data));
            await _queueReference.AddMessageAsync(message).AnyContext();
            
            var entry = new QueueEntry<T>(message.Id, data, this, DateTime.UtcNow, 0);
            await OnEnqueuedAsync(entry).AnyContext();
            
            return message.Id;
        }

        protected override async Task<IQueueEntry<T>> DequeueImplAsync(CancellationToken cancellationToken) {
            // TODO: Use cancellation token overloads
            var linkedCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(_queueDisposedCancellationTokenSource.Token, cancellationToken).Token;

            // TODO Pass linkedCancellationToken to GetMessageAsync once weird timeout issue is resolved.
            var message = await _queueReference.GetMessageAsync(_workItemTimeout, null, null).AnyContext();

            while (message == null && !linkedCancellationToken.IsCancellationRequested) {
                try {
                    await Task.Delay(_dequeueInterval, linkedCancellationToken);
                } catch (TaskCanceledException) { }

                // TODO Pass linkedCancellationToken to GetMessageAsync once weird timeout issue is resolved.
                message = await _queueReference.GetMessageAsync(_workItemTimeout, null, null).AnyContext();
            }

            if (message == null)
                return null;

            Interlocked.Increment(ref _dequeuedCount);
            var data = await _serializer.DeserializeAsync<T>(message.AsBytes);
            var entry = new AzureStorageQueueEntry<T>(message, data, this);
            await OnDequeuedAsync(entry).AnyContext();
            return entry;
        }

        public override async Task RenewLockAsync(IQueueEntry<T> queueEntry) {
            var azureQueueEntry = ToAzureEntryWithCheck(queueEntry);
            await _queueReference.UpdateMessageAsync(azureQueueEntry.UnderlyingMessage, _workItemTimeout, MessageUpdateFields.Visibility).AnyContext();
            await OnLockRenewedAsync(queueEntry).AnyContext();
        }

        public override async Task CompleteAsync(IQueueEntry<T> queueEntry) {
            var azureQueueEntry = ToAzureEntryWithCheck(queueEntry);
            await _queueReference.DeleteMessageAsync(azureQueueEntry.UnderlyingMessage).AnyContext();

            Interlocked.Increment(ref _completedCount);
            await OnCompletedAsync(queueEntry).AnyContext();
        }

        public override async Task AbandonAsync(IQueueEntry<T> queueEntry) {
            var azureQueueEntry = ToAzureEntryWithCheck(queueEntry);

            if (azureQueueEntry.Attempts > _retries) {
                await _queueReference.DeleteMessageAsync(azureQueueEntry.UnderlyingMessage).AnyContext();
                await _deadletterQueueReference.AddMessageAsync(azureQueueEntry.UnderlyingMessage).AnyContext();
            }
            else {
                // Make the item visible immediately
                await _queueReference.UpdateMessageAsync(azureQueueEntry.UnderlyingMessage, TimeSpan.Zero, MessageUpdateFields.Visibility).AnyContext();
            }

            Interlocked.Increment(ref _abandonedCount);
            await OnAbandonedAsync(queueEntry).AnyContext();
        }

        protected override Task<IEnumerable<T>> GetDeadletterItemsImplAsync(CancellationToken cancellationToken) {
            throw new NotImplementedException("Azure Storage Queues do not support retrieving the entire queue");
        }

        protected override async Task<QueueStats> GetQueueStatsImplAsync() {
            await _queueReference.FetchAttributesAsync().AnyContext();
            await _deadletterQueueReference.FetchAttributesAsync().AnyContext();

            return new QueueStats {
                Queued = _queueReference.ApproximateMessageCount.GetValueOrDefault(),
                Working = 0,
                Deadletter = _deadletterQueueReference.ApproximateMessageCount.GetValueOrDefault(),
                Enqueued = _enqueuedCount,
                Dequeued = _dequeuedCount,
                Completed = _completedCount,
                Abandoned = _abandonedCount,
                Errors = _workerErrorCount,
                Timeouts = 0
            };
        }

        public override async Task DeleteQueueAsync() {
            await _queueReference.DeleteIfExistsAsync().AnyContext();
            await _deadletterQueueReference.DeleteIfExistsAsync().AnyContext();

            _enqueuedCount = 0;
            _dequeuedCount = 0;
            _completedCount = 0;
            _abandonedCount = 0;
            _workerErrorCount = 0;
        }

        protected override void StartWorkingImpl(Func<IQueueEntry<T>, CancellationToken, Task> handler, bool autoComplete = false, CancellationToken cancellationToken = new CancellationToken()) {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));
            
            var linkedCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(_queueDisposedCancellationTokenSource.Token, cancellationToken).Token;

            Task.Run(async () => {
                while (!linkedCancellationToken.IsCancellationRequested) {
                    IQueueEntry<T> queueEntry = null;
                    try {
                        queueEntry = await DequeueImplAsync(cancellationToken).AnyContext();
                    } catch (TaskCanceledException) { }

                    if (linkedCancellationToken.IsCancellationRequested || queueEntry == null)
                        continue;

                    try { 
                        await handler(queueEntry, cancellationToken);
                        if (autoComplete && !queueEntry.IsAbandoned && !queueEntry.IsCompleted)
                            await queueEntry.CompleteAsync().AnyContext();
                    }
                    catch (Exception ex) {
                        Interlocked.Increment(ref _workerErrorCount);
                        _logger.Error(ex, "Worker error: {0}", ex.Message);

                        if (!queueEntry.IsAbandoned && !queueEntry.IsCompleted)
                            await queueEntry.AbandonAsync().AnyContext();
                    }
                }

                _logger.Trace("Worker exiting: {0} Cancel Requested: {1}", _queueReference.Name, linkedCancellationToken.IsCancellationRequested);
            }, linkedCancellationToken);
        }

        public override void Dispose() {
            _logger.Trace("Queue {0} dispose", _queueName);

            _queueDisposedCancellationTokenSource?.Cancel();
            base.Dispose();
        }
        
        private static AzureStorageQueueEntry<T> ToAzureEntryWithCheck(IQueueEntry<T> queueEntry) {
            var azureQueueEntry = queueEntry as AzureStorageQueueEntry<T>;

            if (azureQueueEntry == null)
                throw new ArgumentException($"Unknown entry type. Can only process entries of type '{nameof(AzureStorageQueueEntry<T>)}'");

            return azureQueueEntry;
        } 
    }
}
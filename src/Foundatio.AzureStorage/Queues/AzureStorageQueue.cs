using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Queue;
using Microsoft.WindowsAzure.Storage.RetryPolicies;
using Nito.AsyncEx;

namespace Foundatio.Queues {
    public class AzureStorageQueue<T> : QueueBase<T> where T : class {
        private readonly CloudQueue _queueReference;
        private readonly CloudQueue _deadletterQueueReference;
        private long _enqueuedCount;
        private long _dequeuedCount;
        private long _completedCount;
        private long _abandonedCount;
        private long _workerErrorCount;
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

            if (!String.IsNullOrEmpty(queueName))
                _queueName = queueName;

            _queueReference = client.GetQueueReference(queueName);
            _deadletterQueueReference = client.GetQueueReference($"{queueName}-deadletter");

            _retries = retries;
            if (workItemTimeout.HasValue)
                _workItemTimeout = workItemTimeout.Value;
            if (dequeueInterval.HasValue)
                _dequeueInterval = dequeueInterval.Value;
            if (retryPolicy != null) 
                client.DefaultRequestOptions.RetryPolicy = retryPolicy;
        }

        protected override async Task EnsureQueueCreatedAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            if (_queueCreated)
                return;

            using (await _lock.LockAsync(cancellationToken).AnyContext()) {
                if (_queueCreated)
                    return;

                await _queueReference.CreateIfNotExistsAsync(cancellationToken).AnyContext();
                await _deadletterQueueReference.CreateIfNotExistsAsync(cancellationToken).AnyContext();

                _queueCreated = true;
            }
        }

        protected override async Task<string> EnqueueImplAsync(T data) {
            if (!await OnEnqueuingAsync(data).AnyContext())
                return null;

            Interlocked.Increment(ref _enqueuedCount);
            var message = new CloudQueueMessage(await _serializer.SerializeAsync(data).AnyContext());
            await _queueReference.AddMessageAsync(message).AnyContext();
            
            var entry = new QueueEntry<T>(message.Id, data, this, SystemClock.UtcNow, 0);
            await OnEnqueuedAsync(entry).AnyContext();
            
            return message.Id;
        }

        protected override async Task<IQueueEntry<T>> DequeueImplAsync(CancellationToken linkedCancellationToken) {
            // TODO: Pass linkedCancellationToken to GetMessageAsync once weird timeout issue is resolved.
            var message = await _queueReference.GetMessageAsync(_workItemTimeout, null, null).AnyContext();
            _logger.Trace("Initial message id: {0}", message?.Id);

            while (message == null && !linkedCancellationToken.IsCancellationRequested) {
                _logger.Trace("Waiting to dequeue item...");
                var sw = Stopwatch.StartNew();

                try {
                    await SystemClock.SleepAsync(_dequeueInterval, GetDequeueCanncellationToken(linkedCancellationToken)).AnyContext();
                } catch (OperationCanceledException) { }

                sw.Stop();
                _logger.Trace("Waited for dequeue: {0}", sw.Elapsed.ToString());

                // TODO Pass linkedCancellationToken to GetMessageAsync once weird timeout issue is resolved.
                message = await _queueReference.GetMessageAsync(_workItemTimeout, null, null).AnyContext();
                _logger.Trace("Message id: {0}", message?.Id);
            }

            if (message == null)
                return null;

            Interlocked.Increment(ref _dequeuedCount);
            var data = await _serializer.DeserializeAsync<T>(message.AsBytes).AnyContext();
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
            queueEntry.MarkCompleted();
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
            queueEntry.MarkAbandoned();
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

        protected override void StartWorkingImpl(Func<IQueueEntry<T>, CancellationToken, Task> handler, bool autoComplete, CancellationToken cancellationToken) {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            var linkedCancellationToken = GetLinkedDisposableCanncellationToken(cancellationToken);

            Task.Run(async () => {
                _logger.Trace("WorkerLoop Start {_queueName}", _queueName);

                while (!linkedCancellationToken.IsCancellationRequested) {
                    _logger.Trace("WorkerLoop Signaled {_queueName}", _queueName);

                    IQueueEntry<T> queueEntry = null;
                    try {
                        queueEntry = await DequeueImplAsync(linkedCancellationToken).AnyContext();
                    } catch (OperationCanceledException) { }

                    if (linkedCancellationToken.IsCancellationRequested || queueEntry == null)
                        continue;

                    try { 
                        await handler(queueEntry, linkedCancellationToken).AnyContext();
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

        private static AzureStorageQueueEntry<T> ToAzureEntryWithCheck(IQueueEntry<T> queueEntry) {
            var azureQueueEntry = queueEntry as AzureStorageQueueEntry<T>;
            if (azureQueueEntry == null)
                throw new ArgumentException($"Unknown entry type. Can only process entries of type '{nameof(AzureStorageQueueEntry<T>)}'");

            return azureQueueEntry;
        } 
    }
}
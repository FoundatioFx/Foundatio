using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Queue;
using Microsoft.WindowsAzure.Storage.RetryPolicies;

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

        public AzureStorageQueue(string connectionString, string queueName = null, int retries = 2, TimeSpan? workItemTimeout = null, TimeSpan? dequeueInterval = null, IRetryPolicy retryPolicy = null, ISerializer serializer = null, IEnumerable<IQueueBehavior<T>> behaviors = null) : base(serializer, behaviors) {
            var account = CloudStorageAccount.Parse(connectionString);
            var client = account.CreateCloudQueueClient();

            _queueName = queueName;
            _queueReference = client.GetQueueReference(queueName);
            _deadletterQueueReference = client.GetQueueReference(String.Concat(queueName, "-deadletter"));
            
            _queueDisposedCancellationTokenSource = new CancellationTokenSource();

            _retries = retries;

            if (workItemTimeout.HasValue)
                _workItemTimeout = workItemTimeout.Value;
            if (dequeueInterval.HasValue)
                _dequeueInterval = dequeueInterval.Value;
            if (retryPolicy != null) 
                client.DefaultRequestOptions.RetryPolicy = retryPolicy;

            _queueReference.CreateIfNotExists();
            _deadletterQueueReference.CreateIfNotExists();
        }

        public override async Task<string> EnqueueAsync(T data) {
            if (!await OnEnqueuingAsync(data).AnyContext())
                return null;

            Interlocked.Increment(ref _enqueuedCount);
            var message = new CloudQueueMessage(await _serializer.SerializeAsync(data));
            await _queueReference.AddMessageAsync(message).AnyContext();

            var entry = new QueueEntry<T>(message.Id, data, this, DateTime.UtcNow, 0);
            await OnEnqueuedAsync(entry).AnyContext();
            
            return message.Id;
        }

        public override async Task<IQueueEntry<T>> DequeueAsync(CancellationToken cancellationToken = new CancellationToken()) {
            // TODO: Use cancellation token overloads
            var linkedCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(_queueDisposedCancellationTokenSource.Token, cancellationToken).Token;

            // TODO Pass linkedCancellationToken to GetMessageAsync once weird timeout issue is resolved.
            var message = await _queueReference.GetMessageAsync(_workItemTimeout, null, null).AnyContext();

            while (message == null && !linkedCancellationToken.IsCancellationRequested) {
                try {
                    await Task.Delay(_dequeueInterval, linkedCancellationToken);
                }
                catch (TaskCanceledException) { }

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

        public override Task RenewLockAsync(IQueueEntry<T> queueEntry) {
            return TaskHelper.Completed();
        }

        public override async Task CompleteAsync(IQueueEntry<T> queueEntry) {
            var azureQueueEntry = ToAzureEntryWithCheck(queueEntry);
            Interlocked.Increment(ref _completedCount);
            await _queueReference.DeleteMessageAsync(azureQueueEntry.UnderlyingMessage).AnyContext();
            await OnCompletedAsync(queueEntry).AnyContext();
        }

        public override async Task AbandonAsync(IQueueEntry<T> queueEntry) {
            var azureQueueEntry = ToAzureEntryWithCheck(queueEntry);
            Interlocked.Increment(ref _abandonedCount);

            if (azureQueueEntry.Attempts > _retries) {
                await _queueReference.DeleteMessageAsync(azureQueueEntry.UnderlyingMessage).AnyContext();
                await _deadletterQueueReference.AddMessageAsync(azureQueueEntry.UnderlyingMessage).AnyContext();
            }
            else {
                // Make the item visible immediately
                await _queueReference.UpdateMessageAsync(azureQueueEntry.UnderlyingMessage, TimeSpan.Zero, MessageUpdateFields.Visibility).AnyContext();
            }
            
            await OnAbandonedAsync(queueEntry).AnyContext();
        }

        public override Task<IEnumerable<T>> GetDeadletterItemsAsync(CancellationToken cancellationToken = new CancellationToken()) {
            throw new NotImplementedException("Azure Storage Queues do not support retrieving the entire queue");
        }

        public override async Task<QueueStats> GetQueueStatsAsync() {
            await _queueReference.FetchAttributesAsync();
            await _deadletterQueueReference.FetchAttributesAsync();

            return new QueueStats {
                Queued = _queueReference.ApproximateMessageCount.GetValueOrDefault(),
                Working = -1,
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
            // ServiceBusQueue seems to recreate so we're doing the same here.
            // This should probably be renamed to ClearQueueAsync()?
            await _queueReference.ClearAsync().AnyContext();
            await _deadletterQueueReference.ClearAsync().AnyContext();

            _enqueuedCount = 0;
            _dequeuedCount = 0;
            _completedCount = 0;
            _abandonedCount = 0;
            _workerErrorCount = 0;
        }

        public override void StartWorking(Func<IQueueEntry<T>, CancellationToken, Task> handler, bool autoComplete = false, CancellationToken cancellationToken = new CancellationToken()) {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            var linkedCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(_queueDisposedCancellationTokenSource.Token, cancellationToken).Token;

            Task.Run(async () => {
                while (!linkedCancellationToken.IsCancellationRequested) {
                    IQueueEntry<T> queueEntry = null;
                    try {
                        queueEntry = await DequeueAsync(cancellationToken).AnyContext();
                    }
                    catch (TaskCanceledException) { }

                    if (linkedCancellationToken.IsCancellationRequested || queueEntry == null)
                        continue;

                    try { 
                        await handler(queueEntry, cancellationToken);
                        if (autoComplete)
                            await queueEntry.CompleteAsync().AnyContext();
                    }
                    catch (Exception ex) {
                        Logger.Error().Exception(ex).Message("Worker error: {0}", ex.Message).Write();
                        await queueEntry.AbandonAsync().AnyContext();
                        Interlocked.Increment(ref _workerErrorCount);
                    }
                }
#if DEBUG
                Logger.Trace().Message("Worker exiting: {0} Cancel Requested: {1}", _queueReference.Name, linkedCancellationToken.IsCancellationRequested).Write();
#endif
            }, linkedCancellationToken);
        }

        public override void Dispose() {
            Logger.Trace().Message("Queue {0} dispose", _queueName).Write();

            _queueDisposedCancellationTokenSource?.Cancel();

            base.Dispose();
        }
        
        private static AzureStorageQueueEntry<T> ToAzureEntryWithCheck(IQueueEntry<T> queueEntry) {
            var azureQueueEntry = queueEntry as AzureStorageQueueEntry<T>;

            if (azureQueueEntry == null) {
                throw new ArgumentException($"Unknown entry type. Can only process entries of type '{nameof(AzureStorageQueueEntry<T>)}'");
            }

            return azureQueueEntry;
        } 
    }
}
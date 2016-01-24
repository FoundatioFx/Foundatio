using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Queues;
using Foundatio.Serializer;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Queue;
using Microsoft.WindowsAzure.Storage.RetryPolicies;

namespace Foundatio.AzureStorage.Queues {
    public class AzureStorageQueue<T> : QueueBase<T> where T : class {
        private readonly CloudQueue _queueReference;
        private readonly CloudQueue _poisonQueueReference;
        private long _enqueuedCount;
        private long _dequeuedCount;
        private long _completedCount;
        private long _abandonedCount;
        private long _workerErrorCount;
        private readonly CancellationTokenSource _queueDisposedCancellationTokenSource;
        private readonly int _retries;
        private readonly TimeSpan _workItemTimeout = TimeSpan.FromMinutes(5);

        public AzureStorageQueue(string connectionString, string queueName = null, int retries = 2, TimeSpan? workItemTimeout = null, IRetryPolicy retryPolicy = null, ISerializer serializer = null, IEnumerable<IQueueBehavior<T>> behaviors = null) : base(serializer, behaviors) {
            var account = CloudStorageAccount.Parse(connectionString);
            var client = account.CreateCloudQueueClient();

            _queueReference = client.GetQueueReference(queueName);
            _poisonQueueReference = client.GetQueueReference(String.Concat(queueName, "-poison"));
            
            _queueDisposedCancellationTokenSource = new CancellationTokenSource();

            _retries = retries;

            if (workItemTimeout.HasValue)
                _workItemTimeout = workItemTimeout.Value;

            if (retryPolicy != null) 
                client.DefaultRequestOptions.RetryPolicy = retryPolicy;

            _queueReference.CreateIfNotExists();
            _poisonQueueReference.CreateIfNotExists();
        }

        public override async Task<string> EnqueueAsync(T data) {
            if (!await OnEnqueuingAsync(data).AnyContext())
                return null;

            Interlocked.Increment(ref _enqueuedCount);
            var message = new CloudQueueMessage(await _serializer.SerializeAsync(data));
            await _queueReference.AddMessageAsync(message).AnyContext();

            await OnEnqueuedAsync(data, message.Id).AnyContext();
            
            return message.Id;
        }

        public override async Task<QueueEntry<T>> DequeueAsync(CancellationToken cancellationToken = new CancellationToken()) {
            var linkedCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(_queueDisposedCancellationTokenSource.Token, cancellationToken).Token;

            CloudQueueMessage message = null;

            try {
                message = await _queueReference.GetMessageAsync(_workItemTimeout, null, null, linkedCancellationToken).AnyContext();
            }
            catch (TaskCanceledException) { }

            if (message == null)
                return null;

            Interlocked.Increment(ref _dequeuedCount);
            var data = await _serializer.DeserializeAsync<T>(message.AsBytes);
            var entry = new AzureStorageQueueEntry<T>(message, data, this);
            await OnDequeuedAsync(entry).AnyContext();
            return entry;
        }

        public override async Task CompleteAsync(QueueEntry<T> queueEntry) {
            var azureQueueEntry = ToAzureEntryWithCheck(queueEntry);
            Interlocked.Increment(ref _completedCount);
            await _queueReference.DeleteMessageAsync(azureQueueEntry.UnderlyingMessage).AnyContext();
            await OnCompletedAsync(queueEntry.Id).AnyContext();
        }
        public override Task CompleteAsync(string id) {
            throw new NotSupportedException();
        }

        public override async Task AbandonAsync(QueueEntry<T> queueEntry) {
            var azureQueueEntry = ToAzureEntryWithCheck(queueEntry);
            Interlocked.Increment(ref _abandonedCount);

            if (azureQueueEntry.Attempts > _retries) {
                await _queueReference.DeleteMessageAsync(azureQueueEntry.UnderlyingMessage).AnyContext();
                await _poisonQueueReference.AddMessageAsync(azureQueueEntry.UnderlyingMessage).AnyContext();
            }
            // else wait until visibility expires

            await OnAbandonedAsync(queueEntry.Id).AnyContext();
        }
        public override Task AbandonAsync(string id) {
            throw new NotSupportedException();
        }

        public override Task<IEnumerable<T>> GetDeadletterItemsAsync(CancellationToken cancellationToken = new CancellationToken()) {
            throw new NotImplementedException("Azure Storage Queues do not support retreiving the entire queue");
        }

        public override async Task<QueueStats> GetQueueStatsAsync() {
            await _queueReference.FetchAttributesAsync();
            await _poisonQueueReference.FetchAttributesAsync();

            return new QueueStats {
                Queued = _queueReference.ApproximateMessageCount.GetValueOrDefault(),
                Working = -1,
                Deadletter = _poisonQueueReference.ApproximateMessageCount.GetValueOrDefault(),
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
            await _queueReference.CreateIfNotExistsAsync().AnyContext();

            _enqueuedCount = 0;
            _dequeuedCount = 0;
            _completedCount = 0;
            _abandonedCount = 0;
            _workerErrorCount = 0;
        }

        public override void StartWorking(Func<QueueEntry<T>, CancellationToken, Task> handler, bool autoComplete = false, CancellationToken cancellationToken = new CancellationToken()) {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            var linkedCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(_queueDisposedCancellationTokenSource.Token, cancellationToken).Token;

            Task.Run(async () => {
                while (!linkedCancellationToken.IsCancellationRequested) {
                    QueueEntry<T> queueEntry = null;
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
                        Interlocked.Increment(ref _workerErrorCount);
                        Logger.Error().Exception(ex).Message("Worker error: {0}", ex.Message).Write();
                        await queueEntry.AbandonAsync();
                    }
                }
#if DEBUG
                Logger.Trace().Message("Worker exiting: {0} Cancel Requested: {1}", _queueReference.Name, linkedCancellationToken.IsCancellationRequested).Write();
#endif
            }, linkedCancellationToken);
        }

        private string MessageToIdString(CloudQueueMessage message) => String.Concat(message.Id, ":", message.PopReceipt);

        private CloudQueueMessage IdStringToMessage(string id) {
            var parts = id.Split(':');

            const string exceptionMessage = "Expected string in format { id}:{ popReceipt}";
            
            if (parts.Length < 2) {
                throw new ArgumentException(exceptionMessage, nameof(id));
            }

            if (parts.Length > 2) {
                throw new ArgumentException(String.Concat(exceptionMessage, ". Multiple ':' found"));
            }

            string messageId = parts[0];
            string popReceipt = parts[1];

            return new CloudQueueMessage(messageId, popReceipt);
        }

        private static AzureStorageQueueEntry<T> ToAzureEntryWithCheck(QueueEntry<T> queueEntry) {
            var azureQueueEntry = queueEntry as AzureStorageQueueEntry<T>;

            if (azureQueueEntry == null) {
                throw new ArgumentException($"Unknown entry type. Can only process entries of type '{nameof(AzureStorageQueueEntry<T>)}'");
            }

            return azureQueueEntry;
        } 
    }
}
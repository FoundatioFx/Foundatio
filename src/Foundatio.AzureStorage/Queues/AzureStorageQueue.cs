using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
using Foundatio.Extensions;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Queue;

namespace Foundatio.Queues {
    public class AzureStorageQueue<T> : QueueBase<T, AzureStorageQueueOptions<T>> where T : class {
        private readonly AsyncLock _lock = new AsyncLock();
        private readonly CloudQueue _queueReference;
        private readonly CloudQueue _deadletterQueueReference;
        private long _enqueuedCount;
        private long _dequeuedCount;
        private long _completedCount;
        private long _abandonedCount;
        private long _workerErrorCount;
        private bool _queueCreated;

        public AzureStorageQueue(AzureStorageQueueOptions<T> options) : base(options) {
            if (String.IsNullOrEmpty(options.ConnectionString))
                throw new ArgumentException("ConnectionString is required.");

            var account = CloudStorageAccount.Parse(options.ConnectionString);
            var client = account.CreateCloudQueueClient();
            if (options.RetryPolicy != null)
                client.DefaultRequestOptions.RetryPolicy = options.RetryPolicy;

            _queueReference = client.GetQueueReference(_options.Name);
            _deadletterQueueReference = client.GetQueueReference($"{_options.Name}-poison");
        }

        protected override async Task EnsureQueueCreatedAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            if (_queueCreated)
                return;

            using (await _lock.LockAsync().AnyContext()) {
                if (_queueCreated)
                    return;

                var sw = Stopwatch.StartNew();
                await Task.WhenAll(
                    _queueReference.CreateIfNotExistsAsync(),
                    _deadletterQueueReference.CreateIfNotExistsAsync()
                ).AnyContext();
                _queueCreated = true;

                sw.Stop();
                _logger.LogTrace("Ensure queue exists took {0}ms.", sw.ElapsedMilliseconds);
            }
        }

        protected override async Task<string> EnqueueImplAsync(T data) {
            if (!await OnEnqueuingAsync(data).AnyContext())
                return null;

            Interlocked.Increment(ref _enqueuedCount);
            var message = new CloudQueueMessage(_serializer.SerializeToString(data));
            await _queueReference.AddMessageAsync(message).AnyContext();

            var entry = new QueueEntry<T>(message.Id, data, this, SystemClock.UtcNow, 0);
            await OnEnqueuedAsync(entry).AnyContext();

            return message.Id;
        }

        protected override async Task<IQueueEntry<T>> DequeueImplAsync(CancellationToken linkedCancellationToken) {
            var message = await _queueReference.GetMessageAsync(_options.WorkItemTimeout, null, null).AnyContext();
            _logger.LogTrace("Initial message id: {0}", message?.Id ?? "<null>");

            while (message == null && !linkedCancellationToken.IsCancellationRequested) {
                _logger.LogTrace("Waiting to dequeue item...");
                var sw = Stopwatch.StartNew();

                try {
                    if (!linkedCancellationToken.IsCancellationRequested)
                        await SystemClock.SleepAsync(_options.DequeueInterval, linkedCancellationToken).AnyContext();
                } catch (OperationCanceledException) { }

                sw.Stop();
                _logger.LogTrace("Waited for dequeue: {0}", sw.Elapsed.ToString());

                message = await _queueReference.GetMessageAsync(_options.WorkItemTimeout,  null, null).AnyContext();
                _logger.LogTrace("Message id: {0}", message?.Id ?? "<null>");
            }

            if (message == null)
                return null;

            Interlocked.Increment(ref _dequeuedCount);
            var data = _serializer.Deserialize<T>(message.AsBytes);
            var entry = new AzureStorageQueueEntry<T>(message, data, this);
            await OnDequeuedAsync(entry).AnyContext();
            return entry;
        }

        public override async Task RenewLockAsync(IQueueEntry<T> entry) {
            _logger.LogDebug("Queue {0} renew lock item: {1}", _options.Name, entry.Id);
            var azureQueueEntry = ToAzureEntryWithCheck(entry);
            await _queueReference.UpdateMessageAsync(azureQueueEntry.UnderlyingMessage, _options.WorkItemTimeout, MessageUpdateFields.Visibility).AnyContext();
            await OnLockRenewedAsync(entry).AnyContext();
            _logger.LogTrace("Renew lock done: {0}", entry.Id);
        }

        public override async Task CompleteAsync(IQueueEntry<T> entry) {
            _logger.LogDebug("Queue {0} complete item: {1}", _options.Name, entry.Id);
            if (entry.IsAbandoned || entry.IsCompleted)
                throw new InvalidOperationException("Queue entry has already been completed or abandoned.");

            var azureQueueEntry = ToAzureEntryWithCheck(entry);
            await _queueReference.DeleteMessageAsync(azureQueueEntry.UnderlyingMessage).AnyContext();

            Interlocked.Increment(ref _completedCount);
            entry.MarkCompleted();
            await OnCompletedAsync(entry).AnyContext();
            _logger.LogTrace("Complete done: {0}", entry.Id);
        }

        public override async Task AbandonAsync(IQueueEntry<T> entry) {
            _logger.LogDebug("Queue {_options.Name}:{QueueId} abandon item: {entryId}", _options.Name, QueueId, entry.Id);
            if (entry.IsAbandoned || entry.IsCompleted)
                throw new InvalidOperationException("Queue entry has already been completed or abandoned.");

            var azureQueueEntry = ToAzureEntryWithCheck(entry);
            if (azureQueueEntry.Attempts > _options.Retries) {
                await Task.WhenAll(
                    _queueReference.DeleteMessageAsync(azureQueueEntry.UnderlyingMessage), 
                    _deadletterQueueReference.AddMessageAsync(azureQueueEntry.UnderlyingMessage)
                ).AnyContext();
            } else {
                // Make the item visible immediately
                await _queueReference.UpdateMessageAsync(azureQueueEntry.UnderlyingMessage, TimeSpan.Zero, MessageUpdateFields.Visibility).AnyContext();
            }

            Interlocked.Increment(ref _abandonedCount);
            entry.MarkAbandoned();
            await OnAbandonedAsync(entry).AnyContext();
            _logger.LogTrace("Abandon complete: {entryId}", entry.Id);
        }

        protected override Task<IEnumerable<T>> GetDeadletterItemsImplAsync(CancellationToken cancellationToken) {
            throw new NotImplementedException("Azure Storage Queues do not support retrieving the entire queue");
        }

        protected override async Task<QueueStats> GetQueueStatsImplAsync() {
            var sw = Stopwatch.StartNew();
            await Task.WhenAll(
                _queueReference.FetchAttributesAsync(),
                _deadletterQueueReference.FetchAttributesAsync()
            ).AnyContext();
            sw.Stop();
            _logger.LogTrace("Fetching stats took {0}ms.", sw.ElapsedMilliseconds);

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
            var sw = Stopwatch.StartNew();
            await Task.WhenAll(
                _queueReference.DeleteIfExistsAsync(),
                _deadletterQueueReference.DeleteIfExistsAsync()
            ).AnyContext();
            _queueCreated = false;

            _enqueuedCount = 0;
            _dequeuedCount = 0;
            _completedCount = 0;
            _abandonedCount = 0;
            _workerErrorCount = 0;

            sw.Stop();
            _logger.LogTrace("Deleting queue took {0}ms.", sw.ElapsedMilliseconds);
        }

        protected override void StartWorkingImpl(Func<IQueueEntry<T>, CancellationToken, Task> handler, bool autoComplete, CancellationToken cancellationToken) {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            var linkedCancellationToken = GetLinkedDisposableCanncellationToken(cancellationToken);

            Task.Run(async () => {
                _logger.LogTrace("WorkerLoop Start {_options.Name}", _options.Name);

                while (!linkedCancellationToken.IsCancellationRequested) {
                    _logger.LogTrace("WorkerLoop Signaled {_options.Name}", _options.Name);

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
                        _logger.LogError(ex, "Worker error: {0}", ex.Message);

                        if (!queueEntry.IsAbandoned && !queueEntry.IsCompleted)
                            await queueEntry.AbandonAsync().AnyContext();
                    }
                }

                _logger.LogTrace("Worker exiting: {0} Cancel Requested: {1}", _queueReference.Name, linkedCancellationToken.IsCancellationRequested);
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
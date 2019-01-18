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
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

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

        public AzureStorageQueue(Builder<AzureStorageQueueOptionsBuilder<T>, AzureStorageQueueOptions<T>> config)
            : this(config(new AzureStorageQueueOptionsBuilder<T>()).Build()) { }

        protected override async Task EnsureQueueCreatedAsync(CancellationToken cancellationToken = default) {
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
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Ensure queue exists took {Elapsed:g}.", sw.Elapsed);
            }
        }

        protected override async Task<string> EnqueueImplAsync(T data) {
            if (!await OnEnqueuingAsync(data).AnyContext())
                return null;

            Interlocked.Increment(ref _enqueuedCount);
            var message = CloudQueueMessage.CreateCloudQueueMessageFromByteArray(_serializer.SerializeToBytes(data));
            await _queueReference.AddMessageAsync(message).AnyContext();

            var entry = new QueueEntry<T>(message.Id, data, this, SystemClock.UtcNow, 0);
            await OnEnqueuedAsync(entry).AnyContext();

            return message.Id;
        }

        protected override async Task<IQueueEntry<T>> DequeueImplAsync(CancellationToken linkedCancellationToken) {
            var message = await _queueReference.GetMessageAsync(_options.WorkItemTimeout, null, null).AnyContext();
            bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
            
            if (message == null) {
                var sw = Stopwatch.StartNew();
                var lastReport = DateTime.Now;
                if (isTraceLogLevelEnabled) _logger.LogTrace("No message available to dequeue, waiting...");

                while (message == null && !linkedCancellationToken.IsCancellationRequested) {
                    if (isTraceLogLevelEnabled && DateTime.Now.Subtract(lastReport) > TimeSpan.FromSeconds(10))
                         _logger.LogTrace("Still waiting for message to dequeue: {Elapsed:g}", sw.Elapsed);

                    try {
                        if (!linkedCancellationToken.IsCancellationRequested)
                            await SystemClock.SleepAsync(_options.DequeueInterval, linkedCancellationToken).AnyContext();
                    } catch (OperationCanceledException) { }

                    message = await _queueReference.GetMessageAsync(_options.WorkItemTimeout,  null, null).AnyContext();
                }

                sw.Stop();
                if (isTraceLogLevelEnabled) _logger.LogTrace("Waited to dequeue message: {Elapsed:g}", sw.Elapsed);
            }

            if (message == null) {
                if (isTraceLogLevelEnabled) _logger.LogTrace("No message was dequeued.");
                return null;
            }

            if (isTraceLogLevelEnabled) _logger.LogTrace("Dequeued message {Id}", message.Id);
            Interlocked.Increment(ref _dequeuedCount);
            var data = _serializer.Deserialize<T>(message.AsBytes);
            var entry = new AzureStorageQueueEntry<T>(message, data, this);
            await OnDequeuedAsync(entry).AnyContext();
            return entry;
        }

        public override async Task RenewLockAsync(IQueueEntry<T> entry) {
            if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("Queue {Name} renew lock item: {EntryId}", _options.Name, entry.Id);
            var azureQueueEntry = ToAzureEntryWithCheck(entry);
            await _queueReference.UpdateMessageAsync(azureQueueEntry.UnderlyingMessage, _options.WorkItemTimeout, MessageUpdateFields.Visibility).AnyContext();
            await OnLockRenewedAsync(entry).AnyContext();
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Renew lock done: {EntryId}", entry.Id);
        }

        public override async Task CompleteAsync(IQueueEntry<T> entry) {
            if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("Queue {Name} complete item: {EntryId}", _options.Name, entry.Id);
            if (entry.IsAbandoned || entry.IsCompleted)
                throw new InvalidOperationException("Queue entry has already been completed or abandoned.");

            var azureQueueEntry = ToAzureEntryWithCheck(entry);
            await _queueReference.DeleteMessageAsync(azureQueueEntry.UnderlyingMessage).AnyContext();

            Interlocked.Increment(ref _completedCount);
            entry.MarkCompleted();
            await OnCompletedAsync(entry).AnyContext();
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Complete done: {EntryId}", entry.Id);
        }

        public override async Task AbandonAsync(IQueueEntry<T> entry) {
            if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("Queue {Name}:{QueueId} abandon item: {EntryId}", _options.Name, QueueId, entry.Id);
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
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Abandon complete: {EntryId}", entry.Id);
        }

        protected override Task<IEnumerable<T>> GetDeadletterItemsImplAsync(CancellationToken cancellationToken) {
            throw new NotImplementedException("Azure Storage Queues do not support retrieving the entire queue");
        }

        protected override async Task<QueueStats> GetQueueStatsImplAsync() {
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Fetching stats.");
            var sw = Stopwatch.StartNew();
            await Task.WhenAll(
                _queueReference.FetchAttributesAsync(),
                _deadletterQueueReference.FetchAttributesAsync()
            ).AnyContext();
            sw.Stop();
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Fetching stats took {Elapsed:g}.", sw.Elapsed);

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
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Deleting queue took {Elapsed:g}.", sw.Elapsed);
        }

        protected override void StartWorkingImpl(Func<IQueueEntry<T>, CancellationToken, Task> handler, bool autoComplete, CancellationToken cancellationToken) {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            var linkedCancellationToken = GetLinkedDisposableCanncellationTokenSource(cancellationToken);

            Task.Run(async () => {
                bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
                if (isTraceLogLevelEnabled) _logger.LogTrace("WorkerLoop Start {Name}", _options.Name);

                while (!linkedCancellationToken.IsCancellationRequested) {
                    if (isTraceLogLevelEnabled) _logger.LogTrace("WorkerLoop Signaled {Name}", _options.Name);

                    IQueueEntry<T> queueEntry = null;
                    try {
                        queueEntry = await DequeueImplAsync(linkedCancellationToken.Token).AnyContext();
                    } catch (OperationCanceledException) { }

                    if (linkedCancellationToken.IsCancellationRequested || queueEntry == null)
                        continue;

                    try {
                        await handler(queueEntry, linkedCancellationToken.Token).AnyContext();
                        if (autoComplete && !queueEntry.IsAbandoned && !queueEntry.IsCompleted)
                            await queueEntry.CompleteAsync().AnyContext();
                    }
                    catch (Exception ex) {
                        Interlocked.Increment(ref _workerErrorCount);
                        if (_logger.IsEnabled(LogLevel.Error))
                            _logger.LogError(ex, "Worker error: {Message}", ex.Message);

                        if (!queueEntry.IsAbandoned && !queueEntry.IsCompleted)
                            await queueEntry.AbandonAsync().AnyContext();
                    }
                }

                if (isTraceLogLevelEnabled) _logger.LogTrace("Worker exiting: {Name} Cancel Requested: {IsCancellationRequested}", _queueReference.Name, linkedCancellationToken.IsCancellationRequested);
            }, linkedCancellationToken.Token).ContinueWith(t => linkedCancellationToken.Dispose());
        }

        private static AzureStorageQueueEntry<T> ToAzureEntryWithCheck(IQueueEntry<T> queueEntry) {
            if (!(queueEntry is AzureStorageQueueEntry<T> azureQueueEntry))
                throw new ArgumentException($"Unknown entry type. Can only process entries of type '{nameof(AzureStorageQueueEntry<T>)}'");

            return azureQueueEntry;
        }
    }
}
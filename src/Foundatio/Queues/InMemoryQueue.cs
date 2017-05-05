using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Logging;
using Foundatio.Serializer;
using Foundatio.Utility;
using Nito.AsyncEx;

namespace Foundatio.Queues {
    public class InMemoryQueue<T> : QueueBase<T, InMemoryQueueOptions<T>> where T : class {
        private readonly ConcurrentQueue<QueueEntry<T>> _queue = new ConcurrentQueue<QueueEntry<T>>();
        private readonly ConcurrentDictionary<string, QueueEntry<T>> _dequeued = new ConcurrentDictionary<string, QueueEntry<T>>();
        private readonly ConcurrentQueue<QueueEntry<T>> _deadletterQueue = new ConcurrentQueue<QueueEntry<T>>();
        private readonly AsyncAutoResetEvent _autoResetEvent = new AsyncAutoResetEvent();

        private int _enqueuedCount;
        private int _dequeuedCount;
        private int _completedCount;
        private int _abandonedCount;
        private int _workerErrorCount;
        private int _workerItemTimeoutCount;

        [Obsolete("Use the options overload")]
        public InMemoryQueue(int retries = 2, TimeSpan? retryDelay = null, int[] retryMultipliers = null, TimeSpan? workItemTimeout = null, ISerializer serializer = null, IEnumerable<IQueueBehavior<T>> behaviors = null, ILoggerFactory loggerFactory = null)
            : this(new InMemoryQueueOptions<T> {
                Retries = retries,
                RetryDelay = retryDelay.GetValueOrDefault(TimeSpan.FromMinutes(1)),
                RetryMultipliers = retryMultipliers ?? new[] { 1, 3, 5, 10 },
                WorkItemTimeout = workItemTimeout.GetValueOrDefault(TimeSpan.FromMinutes(5)),
                Behaviors = behaviors,
                Serializer = serializer,
                LoggerFactory = loggerFactory
            }) { }

        public InMemoryQueue(InMemoryQueueOptions<T> options) : base(options) {
            InitializeMaintenance();
        }

        protected override Task EnsureQueueCreatedAsync(CancellationToken cancellationToken = new CancellationToken()) {
            return Task.CompletedTask;
        }

        protected override Task<QueueStats> GetQueueStatsImplAsync() {
            return Task.FromResult(new QueueStats {
                Queued = _queue.Count,
                Working = _dequeued.Count,
                Deadletter = _deadletterQueue.Count,
                Enqueued = _enqueuedCount,
                Dequeued = _dequeuedCount,
                Completed = _completedCount,
                Abandoned = _abandonedCount,
                Errors = _workerErrorCount,
                Timeouts = _workerItemTimeoutCount
            });
        }

        protected override async Task<string> EnqueueImplAsync(T data) {
            string id = Guid.NewGuid().ToString("N");
            _logger.Trace("Queue {0} enqueue item: {1}", _options.Name, id);

            if (!await OnEnqueuingAsync(data).AnyContext())
                return null;

            var entry = new QueueEntry<T>(id, data.DeepClone(), this, SystemClock.UtcNow, 0);
            _queue.Enqueue(entry);
            _logger.Trace("Enqueue: Set Event");

            _autoResetEvent.Set();
            Interlocked.Increment(ref _enqueuedCount);

            await OnEnqueuedAsync(entry).AnyContext();
            _logger.Trace("Enqueue done");

            return id;
        }

        protected override void StartWorkingImpl(Func<IQueueEntry<T>, CancellationToken, Task> handler, bool autoComplete, CancellationToken cancellationToken) {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            _logger.Trace("Queue {0} start working", _options.Name);
            var linkedCancellationToken = GetLinkedDisposableCanncellationToken(cancellationToken);

            Task.Run(async () => {
                _logger.Trace("WorkerLoop Start {0}", _options.Name);

                while (!linkedCancellationToken.IsCancellationRequested) {
                    _logger.Trace("WorkerLoop Signaled {0}", _options.Name);

                    IQueueEntry<T> queueEntry = null;
                    try {
                        queueEntry = await DequeueImplAsync(linkedCancellationToken).AnyContext();
                    } catch (Exception ex) {
                        _logger.Error(ex, "Error on Dequeue: " + ex.Message);
                    }

                    if (linkedCancellationToken.IsCancellationRequested || queueEntry == null)
                        return;

                    try {
                        await handler(queueEntry, linkedCancellationToken).AnyContext();
                        if (autoComplete && !queueEntry.IsAbandoned && !queueEntry.IsCompleted)
                            await queueEntry.CompleteAsync().AnyContext();
                    } catch (Exception ex) {
                        _logger.Error(ex, "Worker error: {0}", ex.Message);
                        if (!queueEntry.IsAbandoned && !queueEntry.IsCompleted)
                            await queueEntry.AbandonAsync().AnyContext();

                        Interlocked.Increment(ref _workerErrorCount);
                    }
                }

                _logger.Trace("Worker exiting: {0} Cancel Requested: {1}", _options.Name, linkedCancellationToken.IsCancellationRequested);
            }, linkedCancellationToken);
        }

        protected override async Task<IQueueEntry<T>> DequeueImplAsync(CancellationToken linkedCancellationToken) {
            _logger.Trace("Queue {type} dequeuing item...", _options.Name);
            _logger.Trace("Queue count: {0}", _queue.Count);

            while (_queue.Count == 0 && !linkedCancellationToken.IsCancellationRequested) {
                _logger.Trace("Waiting to dequeue item...");
                var sw = Stopwatch.StartNew();

                try {
                    await _autoResetEvent.WaitAsync(GetDequeueCanncellationToken(linkedCancellationToken)).AnyContext();
                } catch (OperationCanceledException) { }

                sw.Stop();
                _logger.Trace("Waited for dequeue: {0}", sw.Elapsed.ToString());
            }

            if (_queue.Count == 0)
                return null;

            _logger.Trace("Dequeue: Attempt");
            if (!_queue.TryDequeue(out QueueEntry<T> info) || info == null)
                return null;

            info.Attempts++;
            info.DequeuedTimeUtc = SystemClock.UtcNow;

            if (!_dequeued.TryAdd(info.Id, info))
                throw new Exception("Unable to add item to the dequeued list.");

            Interlocked.Increment(ref _dequeuedCount);
            _logger.Trace("Dequeue: Got Item");

            var entry = new QueueEntry<T>(info.Id, info.Value.DeepClone(), this, info.EnqueuedTimeUtc, info.Attempts);
            await OnDequeuedAsync(entry).AnyContext();
            ScheduleNextMaintenance(SystemClock.UtcNow.Add(_options.WorkItemTimeout));

            return entry;
        }

        public override async Task RenewLockAsync(IQueueEntry<T> entry) {
            _logger.Debug("Queue {0} renew lock item: {1}", _options.Name, entry.Id);

            var item = entry as QueueEntry<T>;
            _dequeued.AddOrUpdate(entry.Id, item, (key, value) => {
                if (item != null)
                    value.RenewedTimeUtc = item.RenewedTimeUtc;

                return value;
            });

            await OnLockRenewedAsync(entry).AnyContext();
            _logger.Trace("Renew lock done: {0}", entry.Id);
        }

        public override async Task CompleteAsync(IQueueEntry<T> entry) {
            _logger.Debug("Queue {0} complete item: {1}", _options.Name, entry.Id);
            if (entry.IsAbandoned || entry.IsCompleted)
                throw new InvalidOperationException("Queue entry has already been completed or abandoned.");

            if (!_dequeued.TryRemove(entry.Id, out QueueEntry<T> info) || info == null)
                throw new Exception("Unable to remove item from the dequeued list.");

            Interlocked.Increment(ref _completedCount);
            entry.MarkCompleted();
            await OnCompletedAsync(entry).AnyContext();
            _logger.Trace("Complete done: {0}", entry.Id);
        }

        public override async Task AbandonAsync(IQueueEntry<T> entry) {
            _logger.Debug("Queue {_options.Name}:{QueueId} abandon item: {entryId}", _options.Name, QueueId, entry.Id);
            if (entry.IsAbandoned || entry.IsCompleted)
                throw new InvalidOperationException("Queue entry has already been completed or abandoned.");

            if (!_dequeued.TryRemove(entry.Id, out QueueEntry<T> info) || info == null)
                throw new Exception("Unable to remove item from the dequeued list.");

            if (info.Attempts < _options.Retries + 1) {
                if (_options.RetryDelay > TimeSpan.Zero) {
                    _logger.Trace("Adding item to wait list for future retry: {0}", entry.Id);
                    var unawaited = Run.DelayedAsync(GetRetryDelay(info.Attempts), () => RetryAsync(info));
                } else {
                    _logger.Trace("Adding item back to queue for retry: {0}", entry.Id);
                    var unawaited = Task.Run(() => RetryAsync(info));
                }
            } else {
                _logger.Trace("Exceeded retry limit moving to deadletter: {0}", entry.Id);
                _deadletterQueue.Enqueue(info);
            }

            Interlocked.Increment(ref _abandonedCount);
            entry.MarkAbandoned();
            await OnAbandonedAsync(entry).AnyContext();
            _logger.Trace("Abandon complete: {entryId}", entry.Id);
        }

        private Task RetryAsync(QueueEntry<T> entry) {
            _logger.Trace("Queue {0} retrying item: {1} Attempts: {2}", _options.Name, entry.Id, entry.Attempts);
            _queue.Enqueue(entry);
            _autoResetEvent.Set();
            return Task.CompletedTask;
        }

        private TimeSpan GetRetryDelay(int attempts) {
            int maxMultiplier = _options.RetryMultipliers.Length > 0 ? _options.RetryMultipliers.Last() : 1;
            int multiplier = attempts <= _options.RetryMultipliers.Length ? _options.RetryMultipliers[attempts - 1] : maxMultiplier;
            return TimeSpan.FromMilliseconds((int)(_options.RetryDelay.TotalMilliseconds * multiplier));
        }

        protected override Task<IEnumerable<T>> GetDeadletterItemsImplAsync(CancellationToken cancellationToken) {
            return Task.FromResult(_deadletterQueue.Select(i => i.Value));
        }

        public override Task DeleteQueueAsync() {
            _logger.Trace("Deleting queue: {type}", _options.Name);
            _queue.Clear();
            _deadletterQueue.Clear();
            _dequeued.Clear();
            _enqueuedCount = 0;
            _dequeuedCount = 0;
            _completedCount = 0;
            _abandonedCount = 0;
            _workerErrorCount = 0;

            return Task.CompletedTask;
        }

        protected override async Task<DateTime?> DoMaintenanceAsync() {
            DateTime utcNow = SystemClock.UtcNow;
            DateTime minAbandonAt = DateTime.MaxValue;

            try {
                foreach (var entry in _dequeued.Values.ToList()) {
                    var abandonAt = entry.RenewedTimeUtc.Add(_options.WorkItemTimeout);
                    if (abandonAt < utcNow) {
                        _logger.Info("DoMaintenance Abandon: {entryId}", entry.Id);

                        await AbandonAsync(entry).AnyContext();
                        Interlocked.Increment(ref _workerItemTimeoutCount);
                    } else if (abandonAt < minAbandonAt)
                        minAbandonAt = abandonAt;
                }
            } catch (Exception ex) {
                _logger.Error(ex, "DoMaintenance Error: " + ex.Message);
            }

            return minAbandonAt;
        }

        public override void Dispose() {
            base.Dispose();
            _queue.Clear();
            _deadletterQueue.Clear();
            _dequeued.Clear();
        }
    }
}
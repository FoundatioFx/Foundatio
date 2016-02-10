using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Serializer;
using Foundatio.Logging;
using Foundatio.Utility;
using Nito.AsyncEx;

namespace Foundatio.Queues {
    public class InMemoryQueue<T> : QueueBase<T> where T : class {
        private readonly ConcurrentQueue<QueueEntry<T>> _queue = new ConcurrentQueue<QueueEntry<T>>();
        private readonly ConcurrentDictionary<string, QueueEntry<T>> _dequeued = new ConcurrentDictionary<string, QueueEntry<T>>();
        private readonly ConcurrentQueue<QueueEntry<T>> _deadletterQueue = new ConcurrentQueue<QueueEntry<T>>();
        private readonly AsyncMonitor _monitor = new AsyncMonitor();
        private readonly TimeSpan _workItemTimeout = TimeSpan.FromMinutes(10);
        private readonly TimeSpan _retryDelay = TimeSpan.FromMinutes(1);
        private readonly int[] _retryMultipliers = { 1, 3, 5, 10 };
        private readonly int _retries;

        private int _enqueuedCount;
        private int _dequeuedCount;
        private int _completedCount;
        private int _abandonedCount;
        private int _workerErrorCount;
        private int _workerItemTimeoutCount;
        private readonly CancellationTokenSource _disposeTokenSource;

        public InMemoryQueue(int retries = 2, TimeSpan? retryDelay = null, int[] retryMultipliers = null, TimeSpan? workItemTimeout = null, ISerializer serializer = null, IEnumerable<IQueueBehavior<T>> behaviors = null) : base(serializer, behaviors) {
            _retries = retries;
            if (retryDelay.HasValue)
                _retryDelay = retryDelay.Value;
            if (retryMultipliers != null)
                _retryMultipliers = retryMultipliers;
            if (workItemTimeout.HasValue)
                _workItemTimeout = workItemTimeout.Value;

            InitializeMaintenance();
            _disposeTokenSource = new CancellationTokenSource();
        }

        public override Task<QueueStats> GetQueueStatsAsync() {
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

        public override async Task<string> EnqueueAsync(T data) {
            string id = Guid.NewGuid().ToString("N");
#if DEBUG
            Logger.Trace().Message("Queue {0} enqueue item: {1}", typeof(T).Name, id).Write();
#endif
            if (!await OnEnqueuingAsync(data).AnyContext())
                return null;

            var entry = new QueueEntry<T>(id, data.Copy(), this, DateTime.UtcNow, 0);
            _queue.Enqueue(entry);
#if DEBUG
            Logger.Trace().Message("Enqueue: Set Event").Write();
#endif
            using (await _monitor.EnterAsync())
                _monitor.Pulse();
            Interlocked.Increment(ref _enqueuedCount);

            await OnEnqueuedAsync(entry).AnyContext();
#if DEBUG
            Logger.Trace().Message("Enqueue done").Write();
#endif

            return id;
        }

        public override void StartWorking(Func<IQueueEntry<T>, CancellationToken, Task> handler, bool autoComplete = false, CancellationToken cancellationToken = default(CancellationToken)) {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            Logger.Trace().Message("Queue {0} start working", typeof(T).Name).Write();

            var linkedCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(_disposeTokenSource.Token, cancellationToken).Token;

            Task.Run(async () => {
#if DEBUG
                Logger.Trace().Message("WorkerLoop Start {0}", typeof(T).Name).Write();
#endif
                while (!linkedCancellationToken.IsCancellationRequested) {
#if DEBUG
                    Logger.Trace().Message("WorkerLoop Signaled {0}", typeof(T).Name).Write();
#endif
                    IQueueEntry<T> queueEntry = null;
                    try {
                        queueEntry = await DequeueAsync(cancellationToken: cancellationToken).AnyContext();
                    } catch (Exception ex) {
                        Logger.Error().Message("Error on Dequeue: " + ex.Message).Exception(ex).Write();
                    }

                    if (queueEntry == null)
                        return;

                    try {
                        await handler(queueEntry, linkedCancellationToken).AnyContext();
                        if (autoComplete)
                            await queueEntry.CompleteAsync().AnyContext();
                    } catch (Exception ex) {
                        Logger.Error().Exception(ex).Message("Worker error: {0}", ex.Message).Write();
                        await queueEntry.AbandonAsync().AnyContext();
                        Interlocked.Increment(ref _workerErrorCount);
                    }
                }
#if DEBUG
                Logger.Trace().Message("WorkLoop End").Write();
#endif
            }, linkedCancellationToken);
        }

        public override async Task<IQueueEntry<T>> DequeueAsync(CancellationToken cancellationToken = default(CancellationToken)) {
#if DEBUG
            Logger.Trace().Message($"Queue {typeof(T).Name} dequeuing item...").Write();
            Logger.Trace().Message("Queue count: {0}", _queue.Count).Write();
#endif
            if (_queue.Count == 0 && !cancellationToken.IsCancellationRequested) {
#if DEBUG
                Logger.Trace().Message("Waiting to dequeue item...").Write();
                var sw = Stopwatch.StartNew();
#endif
                try {
                    using (await _monitor.EnterAsync(cancellationToken))
                        await _monitor.WaitAsync(cancellationToken).AnyContext();
                } catch (TaskCanceledException) {}
#if DEBUG
                sw.Stop();
                Logger.Trace().Message("Waited for dequeue: {0}", sw.Elapsed.ToString()).Write();
#endif
            }

            if (_queue.Count == 0)
                return null;
#if DEBUG
            Logger.Trace().Message("Dequeue: Attempt").Write();
#endif
            QueueEntry<T> info;
            if (!_queue.TryDequeue(out info) || info == null)
                return null;

            info.Attempts++;
            info.DequeuedTimeUtc = DateTime.UtcNow;

            if (!_dequeued.TryAdd(info.Id, info))
                throw new ApplicationException("Unable to add item to the dequeued list.");

            Interlocked.Increment(ref _dequeuedCount);
#if DEBUG
            Logger.Trace().Message("Dequeue: Got Item").Write();
#endif
            var entry = new QueueEntry<T>(info.Id, info.Value.Copy(), this, info.EnqueuedTimeUtc, info.Attempts);
            await OnDequeuedAsync(entry).AnyContext();
            ScheduleNextMaintenance(DateTime.UtcNow.Add(_workItemTimeout));

            return entry;
        }

        public override async Task RenewLockAsync(IQueueEntry<T> entry) {
            await OnLockRenewedAsync(entry).AnyContext();
        }

        public override async Task CompleteAsync(IQueueEntry<T> entry) {
#if DEBUG
            Logger.Trace().Message("Queue {0} complete item: {1}", typeof(T).Name, entry.Id).Write();
#endif
            QueueEntry<T> info = null;
            if (!_dequeued.TryRemove(entry.Id, out info) || info == null)
                throw new ApplicationException("Unable to remove item from the dequeued list.");

            Interlocked.Increment(ref _completedCount);

            await OnCompletedAsync(entry).AnyContext();
#if DEBUG
            Logger.Trace().Message("Complete done: {0}", entry.Id).Write();
#endif
        }

        public override async Task AbandonAsync(IQueueEntry<T> entry) {
#if DEBUG
            Logger.Trace().Message("Queue {0} abandon item: {1}", typeof(T).Name, entry.Id).Write();
#endif
            QueueEntry<T> info;
            if (!_dequeued.TryRemove(entry.Id, out info) || info == null)
                throw new ApplicationException("Unable to remove item from the dequeued list.");

            Interlocked.Increment(ref _abandonedCount);
            if (info.Attempts < _retries + 1) {
                if (_retryDelay > TimeSpan.Zero) {
#if DEBUG
                    Logger.Trace().Message("Adding item to wait list for future retry: {0}", entry.Id).Write();
#endif
                    var unawaited = Run.DelayedAsync(GetRetryDelay(info.Attempts), () => RetryAsync(info));
                } else {
#if DEBUG
                    Logger.Trace().Message("Adding item back to queue for retry: {0}", entry.Id).Write();
#endif
                    var unawaited = Task.Run(() => RetryAsync(info));
                }
            } else {
#if DEBUG
                Logger.Trace().Message("Exceeded retry limit moving to deadletter: {0}", entry.Id).Write();
#endif
                _deadletterQueue.Enqueue(info);
            }

            await OnAbandonedAsync(entry).AnyContext();
#if DEBUG
            Logger.Trace().Message("Abandon complete: {0}", entry.Id).Write();
#endif
        }

        private async Task RetryAsync(QueueEntry<T> entry) {
            _queue.Enqueue(entry);
            using (await _monitor.EnterAsync())
                _monitor.Pulse();
        }

        private TimeSpan GetRetryDelay(int attempts) {
            int maxMultiplier = _retryMultipliers.Length > 0 ? _retryMultipliers.Last() : 1;
            int multiplier = attempts <= _retryMultipliers.Length ? _retryMultipliers[attempts - 1] : maxMultiplier;
            return TimeSpan.FromMilliseconds((int)(_retryDelay.TotalMilliseconds * multiplier));
        }

        public override Task<IEnumerable<T>> GetDeadletterItemsAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            return Task.FromResult(_deadletterQueue.Select(i => i.Value));
        }

        public override Task DeleteQueueAsync() {
            Logger.Trace().Message($"Deleting queue: {typeof(T).Name}").Write();
            _queue.Clear();
            _deadletterQueue.Clear();
            _dequeued.Clear();
            _enqueuedCount = 0;
            _dequeuedCount = 0;
            _completedCount = 0;
            _abandonedCount = 0;
            _workerErrorCount = 0;

            return TaskHelper.Completed();
        }
        
        protected override async Task<DateTime> DoMaintenanceAsync() {
            DateTime utcNow = DateTime.UtcNow;
            DateTime minAbandonAt = DateTime.MaxValue;

            try {
                foreach (var entry in _dequeued.Values.ToList()) {
                    var abandonAt = entry.DequeuedTimeUtc.Add(_workItemTimeout);
                    if (abandonAt < utcNow) {
#if DEBUG
                        Logger.Info().Message($"DoMaintenance Abandon: {entry.Id}").Write();
#endif
                        await AbandonAsync(entry).AnyContext();
                        Interlocked.Increment(ref _workerItemTimeoutCount);
                    } else if (abandonAt < minAbandonAt)
                        minAbandonAt = abandonAt;
                }
            } catch (Exception ex) {
                Logger.Error()
                    .Exception(ex)
                    .Message("DoMaintenance Error: " + ex.Message)
                    .Write();
            }

            return minAbandonAt;
        }

        public override void Dispose() {
            base.Dispose();
            _disposeTokenSource?.Cancel();
        }
    }
}
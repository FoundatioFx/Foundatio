﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Foundatio.AsyncEx;
using Microsoft.Extensions.Logging;

namespace Foundatio.Queues {
    public class InMemoryQueue<T> : QueueBase<T, InMemoryQueueOptions<T>> where T : class {
        private readonly ConcurrentQueue<QueueEntry<T>> _queue = new();
        private readonly ConcurrentDictionary<string, QueueEntry<T>> _dequeued = new();
        private readonly ConcurrentQueue<QueueEntry<T>> _deadletterQueue = new();
        private readonly AsyncAutoResetEvent _autoResetEvent = new();

        private int _enqueuedCount;
        private int _dequeuedCount;
        private int _completedCount;
        private int _abandonedCount;
        private int _workerErrorCount;
        private int _workerItemTimeoutCount;

        public InMemoryQueue() : this(o => o) {}

        public InMemoryQueue(InMemoryQueueOptions<T> options) : base(options) {
            InitializeMaintenance();
        }

        public InMemoryQueue(Builder<InMemoryQueueOptionsBuilder<T>, InMemoryQueueOptions<T>> config)
            : this(config(new InMemoryQueueOptionsBuilder<T>()).Build()) { }

        protected override Task EnsureQueueCreatedAsync(CancellationToken cancellationToken = default) {
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

        public IReadOnlyCollection<QueueEntry<T>> GetEntries() {
            return new ReadOnlyCollection<QueueEntry<T>>(_queue.ToList());
        }

        protected override async Task<string> EnqueueImplAsync(T data, QueueEntryOptions options) {
            string id = Guid.NewGuid().ToString("N");
            _logger.LogTrace("Queue {Name} enqueue item: {Id}", _options.Name, id);

            if (!await OnEnqueuingAsync(data, options).AnyContext())
                return null;

            var entry = new QueueEntry<T>(id, options?.CorrelationId, data.DeepClone(), this, SystemClock.UtcNow, 0);
            entry.Properties.AddRange(options?.Properties);

            Interlocked.Increment(ref _enqueuedCount);

            if (options?.DeliveryDelay != null && options.DeliveryDelay.Value > TimeSpan.Zero) {
                _ = Run.DelayedAsync(options.DeliveryDelay.Value, async () => {
                    _queue.Enqueue(entry);
                    _logger.LogTrace("Enqueue: Set Event");

                    _autoResetEvent.Set();

                    await OnEnqueuedAsync(entry).AnyContext();
                    _logger.LogTrace("Enqueue done");
                }, _queueDisposedCancellationTokenSource.Token);
                return id;
            }
            
            _queue.Enqueue(entry);
            _logger.LogTrace("Enqueue: Set Event");

            _autoResetEvent.Set();

            await OnEnqueuedAsync(entry).AnyContext();
            _logger.LogTrace("Enqueue done");

            return id;
        }

        private readonly List<Task> _workers = new();

        protected override void StartWorkingImpl(Func<IQueueEntry<T>, CancellationToken, Task> handler, bool autoComplete, CancellationToken cancellationToken) {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            _logger.LogTrace("Queue {Name} start working", _options.Name);

            _workers.Add(Task.Run(async () => {
                using var linkedCancellationToken = GetLinkedDisposableCancellationTokenSource(cancellationToken);
                _logger.LogTrace("WorkerLoop Start {Name}", _options.Name);

                while (!linkedCancellationToken.IsCancellationRequested) {
                    _logger.LogTrace("WorkerLoop Signaled {Name}", _options.Name);

                    IQueueEntry<T> queueEntry = null;
                    try {
                        queueEntry = await DequeueImplAsync(linkedCancellationToken.Token).AnyContext();
                    } catch (Exception ex) {
                        _logger.LogError(ex, "Error on Dequeue: {Message}", ex.Message);
                    }

                    if (linkedCancellationToken.IsCancellationRequested || queueEntry == null)
                        return;

                    try {
                        await handler(queueEntry, linkedCancellationToken.Token).AnyContext();
                    } catch (Exception ex) {
                        _logger.LogError(ex, "Worker error: {Message}", ex.Message);

                        if (!queueEntry.IsAbandoned && !queueEntry.IsCompleted) {
                            try {
                                await queueEntry.AbandonAsync().AnyContext();
                            } catch (Exception abandonEx) {
                                _logger.LogError(abandonEx, "Worker error abandoning queue entry: {Message}", abandonEx.Message);
                            }
                        }

                        Interlocked.Increment(ref _workerErrorCount);
                    }

                    if (autoComplete && !queueEntry.IsAbandoned && !queueEntry.IsCompleted) {
                        try {
                            await Run.WithRetriesAsync(() => queueEntry.CompleteAsync(), cancellationToken: linkedCancellationToken.Token, logger: _logger).AnyContext();
                        } catch (Exception ex) {
                            _logger.LogError(ex, "Worker error attempting to auto complete entry: {Message}", ex.Message);
                        }
                    }
                }

                _logger.LogTrace("Worker exiting: {Name} Cancel Requested: {IsCancellationRequested}", _options.Name, linkedCancellationToken.IsCancellationRequested);
            }, GetLinkedDisposableCancellationTokenSource(cancellationToken).Token));
        }

        protected override async Task<IQueueEntry<T>> DequeueImplAsync(CancellationToken linkedCancellationToken) {
            _logger.LogTrace("Queue {Name} dequeuing item... Queue count: {Count}", _options.Name, _queue.Count);

            while (_queue.Count == 0 && !linkedCancellationToken.IsCancellationRequested) {
                _logger.LogTrace("Waiting to dequeue item...");
                var sw = Stopwatch.StartNew();

                try {
                    using var timeoutCancellationTokenSource = new CancellationTokenSource(10000);
                    using var dequeueCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(linkedCancellationToken, timeoutCancellationTokenSource.Token);
                    await _autoResetEvent.WaitAsync(dequeueCancellationTokenSource.Token).AnyContext();
                } catch (OperationCanceledException) { }

                sw.Stop();
                _logger.LogTrace("Waited for dequeue: {Elapsed:g}", sw.Elapsed);
            }

            if (_queue.Count == 0)
                return null;

            _logger.LogTrace("Dequeue: Attempt");
            if (!_queue.TryDequeue(out var entry) || entry == null)
                return null;

            entry.Attempts++;
            entry.DequeuedTimeUtc = SystemClock.UtcNow;

            if (!_dequeued.TryAdd(entry.Id, entry))
                throw new Exception("Unable to add item to the dequeued list");

            Interlocked.Increment(ref _dequeuedCount);
            _logger.LogTrace("Dequeue: Got Item");
            
            await entry.RenewLockAsync();
            await OnDequeuedAsync(entry).AnyContext();
            ScheduleNextMaintenance(SystemClock.UtcNow.Add(_options.WorkItemTimeout));

            return entry;
        }

        public override async Task RenewLockAsync(IQueueEntry<T> entry) {
             _logger.LogDebug("Queue {Name} renew lock item: {Id}", _options.Name, entry.Id);

            if (!_dequeued.TryGetValue(entry.Id, out var targetEntry))
                return;

            targetEntry.RenewedTimeUtc = SystemClock.UtcNow;

            await OnLockRenewedAsync(entry).AnyContext();
            _logger.LogTrace("Renew lock done: {Id}", entry.Id);
        }

        public override async Task CompleteAsync(IQueueEntry<T> entry) {
            _logger.LogDebug("Queue {Name} complete item: {Id}", _options.Name, entry.Id);
            if (entry.IsAbandoned || entry.IsCompleted)
                throw new InvalidOperationException("Queue entry has already been completed or abandoned");

            if (!_dequeued.TryRemove(entry.Id, out var info) || info == null)
                throw new Exception("Unable to remove item from the dequeued list");

            entry.MarkCompleted();
            Interlocked.Increment(ref _completedCount);
            await OnCompletedAsync(entry).AnyContext();
            _logger.LogTrace("Complete done: {Id}", entry.Id);
        }

        public override async Task AbandonAsync(IQueueEntry<T> entry) {
            _logger.LogDebug("Queue {Name}:{QueueId} abandon item: {Id}", _options.Name, QueueId, entry.Id);

            if (entry.IsAbandoned || entry.IsCompleted)
                throw new InvalidOperationException("Queue entry has already been completed or abandoned");

            if (!_dequeued.TryRemove(entry.Id, out var targetEntry) || targetEntry == null)
                throw new Exception("Unable to remove item from the dequeued list");

            entry.MarkAbandoned();
            Interlocked.Increment(ref _abandonedCount);
            _logger.LogTrace("Abandon complete: {Id}", entry.Id);

            try {
                await OnAbandonedAsync(entry).AnyContext();
            } finally {
                if (targetEntry.Attempts < _options.Retries + 1) {
                    if (_options.RetryDelay > TimeSpan.Zero) {
                        _logger.LogTrace("Adding item to wait list for future retry: {Id}", entry.Id);
                        var unawaited = Run.DelayedAsync(GetRetryDelay(targetEntry.Attempts), () => RetryAsync(targetEntry), _queueDisposedCancellationTokenSource.Token);
                    } else {
                        _logger.LogTrace("Adding item back to queue for retry: {Id}", entry.Id);
                        var unawaited = Task.Run(() => RetryAsync(targetEntry));
                    }
                } else {
                    _logger.LogTrace("Exceeded retry limit moving to deadletter: {Id}", entry.Id);
                    _deadletterQueue.Enqueue(targetEntry);
                }
            }
        }

        private Task RetryAsync(QueueEntry<T> entry) {
            _logger.LogTrace("Queue {Name} retrying item: {Id} Attempts: {Attempts}", _options.Name, entry.Id, entry.Attempts);

            entry.Reset();
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
            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("Deleting queue: {Name}", _options.Name);

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
            var utcNow = SystemClock.UtcNow;
            var minAbandonAt = DateTime.MaxValue;

            try {
                foreach (var entry in _dequeued.Values.ToList()) {
                    var abandonAt = entry.RenewedTimeUtc.Add(_options.WorkItemTimeout);
                    if (abandonAt < utcNow) {
                        if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("DoMaintenance Abandon: {Id}", entry.Id);

                        await AbandonAsync(entry).AnyContext();
                        Interlocked.Increment(ref _workerItemTimeoutCount);
                    } else if (abandonAt < minAbandonAt)
                        minAbandonAt = abandonAt;
                }
            } catch (Exception ex) {
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError(ex, "DoMaintenance Error: {Message}", ex.Message);
            }

            return minAbandonAt;
        }

        public override void Dispose() {
            base.Dispose();
            _queue.Clear();
            _deadletterQueue.Clear();
            _dequeued.Clear();

            _logger.LogTrace("Got {WorkerCount} workers to cleanup", _workers.Count);
            foreach (var worker in _workers) {
                if (worker.IsCompleted)
                    continue;

                _logger.LogTrace("Attempting to cleanup worker");
                if (!worker.Wait(TimeSpan.FromSeconds(5)))
                    _logger.LogError("Failed waiting for worker to stop");
            }
        }
    }
}
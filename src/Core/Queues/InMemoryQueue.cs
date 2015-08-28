using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Serializer;
using Foundatio.Utility;
using Foundatio.Logging;
using Nito.AsyncEx;

namespace Foundatio.Queues {
    public class InMemoryQueue<T> : QueueBase<T> where T : class {
        private readonly ConcurrentQueue<QueueInfo<T>> _queue = new ConcurrentQueue<QueueInfo<T>>();
        private readonly ConcurrentDictionary<string, QueueInfo<T>> _dequeued = new ConcurrentDictionary<string, QueueInfo<T>>();
        private readonly ConcurrentQueue<QueueInfo<T>> _deadletterQueue = new ConcurrentQueue<QueueInfo<T>>();
        private readonly AsyncManualResetEvent _autoEvent = new AsyncManualResetEvent(false);
        private readonly TimeSpan _workItemTimeout = TimeSpan.FromMinutes(10);
        private readonly TimeSpan _retryDelay = TimeSpan.FromMinutes(1);
        private readonly int[] _retryMultipliers = { 1, 3, 5, 10 };
        private readonly int _retries = 2;
        private int _enqueuedCount;
        private int _dequeuedCount;
        private int _completedCount;
        private int _abandonedCount;
        private int _workerErrorCount;
        private int _workerItemTimeoutCount;
        private readonly CancellationTokenSource _disposeTokenSource;
        private DateTime? _nextMaintenance = null;
        private readonly Timer _maintenanceTimer;

        public InMemoryQueue(int retries = 2, TimeSpan? retryDelay = null, int[] retryMultipliers = null, TimeSpan? workItemTimeout = null, ISerializer serializer = null, IEnumerable<IQueueBehavior<T>> behaviours = null)
            : base(serializer, behaviours)
        {
            _retries = retries;
            if (retryDelay.HasValue)
                _retryDelay = retryDelay.Value;
            if (retryMultipliers != null)
                _retryMultipliers = retryMultipliers;
            if (workItemTimeout.HasValue)
                _workItemTimeout = workItemTimeout.Value;

            _maintenanceTimer = new Timer(s => DoMaintenance(), null, Timeout.Infinite, Timeout.Infinite);
            _disposeTokenSource = new CancellationTokenSource();
        }

        public override QueueStats GetQueueStats()
        {
            return new QueueStats
            {
                Queued = _queue.Count,
                Working = _dequeued.Count,
                Deadletter = _deadletterQueue.Count,
                Enqueued = _enqueuedCount,
                Dequeued = _dequeuedCount,
                Completed = _completedCount,
                Abandoned = _abandonedCount,
                Errors = _workerErrorCount,
                Timeouts = _workerItemTimeoutCount
            };
        }

        public override string Enqueue(T data) {
            string id = Guid.NewGuid().ToString("N");
            Logger.Trace().Message("Queue {0} enqueue item: {1}", typeof(T).Name, id).Write();
            if (!OnEnqueuing(data))
                return null;

            var info = new QueueInfo<T> {
                Data = data.Copy(),
                Id = id,
                TimeEnqueued = DateTime.UtcNow
            };
            _queue.Enqueue(info);
            Logger.Trace().Message("Enqueue: Set Event").Write();
            _autoEvent.Set();
            Interlocked.Increment(ref _enqueuedCount);

            OnEnqueued(data, id);
            Logger.Trace().Message("Enqueue done").Write();

            return id;
        }

        public override void StartWorking(Action<QueueEntry<T>> handler, bool autoComplete = false, CancellationToken token = default(CancellationToken)) {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            Logger.Trace().Message("Queue {0} start working", typeof(T).Name).Write();

            var tokenSource = CancellationTokenSource.CreateLinkedTokenSource(_disposeTokenSource.Token, token);

            Task.Run(() => WorkerLoop(handler, autoComplete, tokenSource.Token), tokenSource.Token);
        }

        public override QueueEntry<T> Dequeue(TimeSpan? timeout = null, CancellationToken cancellationToken = default(CancellationToken)) {
            Logger.Trace().Message("Queue {0} dequeued item", typeof(T).Name).Write();
            if (!timeout.HasValue)
                timeout = TimeSpan.FromSeconds(30);

            Logger.Trace().Message("Queue count: {0}", _queue.Count).Write();
            if (_queue.Count == 0)
            {
                var sw = new Stopwatch();
                sw.Start();
                Task.WaitAny(Task.Delay(timeout.Value, cancellationToken), _autoEvent.WaitAsync());
                sw.Stop();
                Logger.Trace().Message("Waited for dequeue: timeout={0} actual={1}", timeout.Value.ToString(), sw.Elapsed.ToString()).Write();
            }
            if (_queue.Count == 0 || cancellationToken.IsCancellationRequested)
                return null;

            _autoEvent.Reset();

            Logger.Trace().Message("Dequeue: Attempt").Write();
            QueueInfo<T> info;
            if (!_queue.TryDequeue(out info) || info == null)
                return null;

            info.Attempts++;
            info.TimeDequeued = DateTime.UtcNow;

            if (!_dequeued.TryAdd(info.Id, info))
                throw new ApplicationException("Unable to add item to the dequeued list.");

            Interlocked.Increment(ref _dequeuedCount);
            Logger.Trace().Message("Dequeue: Got Item").Write();
            var entry = new QueueEntry<T>(info.Id, info.Data.Copy(), this, info.TimeEnqueued, info.Attempts);
            OnDequeued(entry);
            ScheduleNextMaintenance(DateTime.UtcNow.Add(_workItemTimeout));

            return entry;
        }

        public override void Complete(IQueueEntryMetadata entry) {
            Logger.Trace().Message("Queue {0} complete item: {1}", typeof(T).Name, entry.Id).Write();

            QueueInfo<T> info = null;
            if (!_dequeued.TryRemove(entry.Id, out info) || info == null)
                throw new ApplicationException("Unable to remove item from the dequeued list.");

            Interlocked.Increment(ref _completedCount);

            OnCompleted(entry);
            Logger.Trace().Message("Complete done: {0}", entry.Id).Write();
        }

        public override void Abandon(IQueueEntryMetadata entry) {
            Logger.Trace().Message("Queue {0} abandon item: {1}", typeof(T).Name, entry.Id).Write();

            QueueInfo<T> info;
            if (!_dequeued.TryRemove(entry.Id, out info) || info == null)
                throw new ApplicationException("Unable to remove item from the dequeued list.");

            Interlocked.Increment(ref _abandonedCount);
            if (info.Attempts < _retries + 1) {
                if (_retryDelay > TimeSpan.Zero) {
                    Logger.Trace().Message("Adding item to wait list for future retry: {0}", entry.Id).Write();
                    Task.Factory.StartNewDelayed(GetRetryDelay(info.Attempts), () => Retry(info));
                } else {
                    Logger.Trace().Message("Adding item back to queue for retry: {0}", entry.Id).Write();
                    Retry(info);
                }
            } else {
                Logger.Trace().Message("Exceeded retry limit moving to deadletter: {0}", entry.Id).Write();
                _deadletterQueue.Enqueue(info);
            }

            OnAbandoned(entry);
            Logger.Trace().Message("Abondon complete: {0}", entry.Id).Write();
        }

        private void Retry(QueueInfo<T> info) {
            _queue.Enqueue(info);
            _autoEvent.Set();
        }

        private int GetRetryDelay(int attempts) {
            int maxMultiplier = _retryMultipliers.Length > 0 ? _retryMultipliers.Last() : 1;
            int multiplier = attempts <= _retryMultipliers.Length ? _retryMultipliers[attempts - 1] : maxMultiplier;
            return (int)(_retryDelay.TotalMilliseconds * multiplier);
        }

        public override IEnumerable<T> GetDeadletterItems() {
            return _deadletterQueue.Select(i => i.Data);
        }

        public override void DeleteQueue() {
            Logger.Trace().Message("Deleting queue: {0}", typeof(T).Name).Write();
            _queue.Clear();
            _deadletterQueue.Clear();
            _dequeued.Clear();
            _enqueuedCount = 0;
            _dequeuedCount = 0;
            _completedCount = 0;
            _abandonedCount = 0;
            _workerErrorCount = 0;
        }

        private Task WorkerLoop(Action<QueueEntry<T>> handler, bool autoComplete, CancellationToken token) {
            Logger.Trace().Message("WorkerLoop Start {0}", typeof(T).Name).Write();
            while (!token.IsCancellationRequested) {
                Logger.Trace().Message("WorkerLoop Signaled {0}", typeof(T).Name).Write();

                QueueEntry<T> queueEntry = null;
                try
                {
                    queueEntry = Dequeue(cancellationToken: token);
                }
                catch (Exception ex)
                {
                    Logger.Error()
                        .Message("Error on Dequeue: " + ex.Message)
                        .Exception(ex)
                        .Write();
                }
              
                if (queueEntry == null)
                    return TaskHelper.Completed();

                try {
                    handler(queueEntry);
                    if (autoComplete)
                        queueEntry.Complete();
                } catch (Exception ex) {
                    Logger.Error().Exception(ex).Message("Worker error: {0}", ex.Message).Write();
                    queueEntry.Abandon();
                    Interlocked.Increment(ref _workerErrorCount);
                }
            }
            Logger.Trace().Message("WorkLoop End").Write();
            return TaskHelper.Completed();
        }

        private void ScheduleNextMaintenance(DateTime value)
        {
            Logger.Trace().Message("ScheduleNextMaintenance: value={0}", value).Write();
            if (value == DateTime.MaxValue)
                return;

            if (_nextMaintenance.HasValue && value > _nextMaintenance.Value)
                return;

            int delay = Math.Max((int)value.Subtract(DateTime.UtcNow).TotalMilliseconds, 0);
            _nextMaintenance = value;
            Logger.Trace().Message("Scheduling maintenance: delay={0}", delay).Write();
            _maintenanceTimer.Change(delay, Timeout.Infinite);
        }

        private void DoMaintenance() {
            Logger.Trace().Message("DoMaintenance {0}", typeof(T).Name).Write();

            DateTime minAbandonAt = DateTime.MaxValue;
            var now = DateTime.UtcNow;
            var abandonedKeys = new List<string>();
            foreach (string key in _dequeued.Keys)
            {
                var abandonAt = _dequeued[key].TimeDequeued.Add(_workItemTimeout);
                if (abandonAt < now)
                    abandonedKeys.Add(key);
                else if (abandonAt < minAbandonAt)
                    minAbandonAt = abandonAt;
            }

            ScheduleNextMaintenance(minAbandonAt);

            if (abandonedKeys.Count == 0)
                return;

            foreach (var key in abandonedKeys)
            {
                Logger.Info().Message("DoMaintenance Abandon: {0}", key).Write();
                var info = _dequeued[key];
                Abandon(new QueueEntry<T>(info.Id, info.Data, this, info.TimeEnqueued, info.Attempts));
                Interlocked.Increment(ref _workerItemTimeoutCount);
            }
        }

        public override void Dispose() {
            base.Dispose();
            _disposeTokenSource?.Cancel();
        }

        private class QueueInfo<TData> {
            public TData Data { get; set; }
            public string Id { get; set; }
            public int Attempts { get; set; }
            public DateTime TimeDequeued { get; set; }
            public DateTime TimeEnqueued { get; set; }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Extensions;
using Foundatio.Lock;
using Foundatio.Serializer;
using Nito.AsyncEx;
using Foundatio.Logging;
using StackExchange.Redis;
#pragma warning disable 4014

namespace Foundatio.Queues {
    public class RedisQueue<T> : QueueBase<T> where T : class {
        private readonly string _queueName;
        protected readonly IDatabase _db;
        protected ISubscriber _subscriber;
        protected readonly RedisCacheClient _cache;
        private long _enqueuedCount;
        private long _dequeuedCount;
        private long _completedCount;
        private long _abandonedCount;
        private long _workerErrorCount;
        private long _workItemTimeoutCount;
        private readonly TimeSpan _payloadTtl;
        private readonly TimeSpan _workItemTimeout = TimeSpan.FromMinutes(10);
        private readonly TimeSpan _retryDelay = TimeSpan.FromMinutes(1);
        private readonly int[] _retryMultipliers = { 1, 3, 5, 10 };
        private readonly int _retries = 2;
        private readonly TimeSpan _deadLetterTtl = TimeSpan.FromDays(1);
        private readonly int _deadLetterMaxItems;
        private readonly CancellationTokenSource _queueDisposedCancellationTokenSource;
        private readonly AsyncMonitor _monitor = new AsyncMonitor();
        protected readonly ILockProvider _maintenanceLockProvider;
        protected readonly bool _runMaintenanceTasks;
        protected Task _maintenanceTask;
        protected static readonly object _lockObject = new object();
        private bool _isSubscribed;

        public RedisQueue(ConnectionMultiplexer connection, ISerializer serializer = null, string queueName = null, int retries = 2, TimeSpan? retryDelay = null, int[] retryMultipliers = null,
            TimeSpan? workItemTimeout = null, TimeSpan? deadLetterTimeToLive = null, int deadLetterMaxItems = 100, bool runMaintenanceTasks = true, IEnumerable<IQueueBehavior<T>> behaviors = null)
            : base(serializer, behaviors) {
            _db = connection.GetDatabase();
            _cache = new RedisCacheClient(connection, _serializer);
            _queueName = queueName ?? typeof(T).Name;
            _queueName = _queueName.RemoveWhiteSpace().Replace(':', '-');
            QueueListName = "q:" + _queueName + ":in";
            WorkListName = "q:" + _queueName + ":work";
            WaitListName = "q:" + _queueName + ":wait";
            DeadListName = "q:" + _queueName + ":dead";
            // TODO: Make queue settings immutable and stored in redis so that multiple clients can't have different settings.
            _retries = retries;
            if (retryDelay.HasValue)
                _retryDelay = retryDelay.Value;
            if (retryMultipliers != null)
                _retryMultipliers = retryMultipliers;
            if (workItemTimeout.HasValue)
                _workItemTimeout = workItemTimeout.Value;
            if (deadLetterTimeToLive.HasValue)
                _deadLetterTtl = deadLetterTimeToLive.Value;
            _deadLetterMaxItems = deadLetterMaxItems;

            _payloadTtl = GetPayloadTtl();

            _queueDisposedCancellationTokenSource = new CancellationTokenSource();
            _subscriber = connection.GetSubscriber();

            _runMaintenanceTasks = runMaintenanceTasks;
            // min is 1 second, max is 1 minute
            TimeSpan interval = _workItemTimeout > TimeSpan.FromSeconds(1) ? _workItemTimeout.Min(TimeSpan.FromMinutes(1)) : TimeSpan.FromSeconds(1);
            _maintenanceLockProvider = new ThrottlingLockProvider(_cache, 1, interval);

            Logger.Trace().Message("Queue {0} created. Retries: {1} Retry Delay: {2}", QueueId, _retries, _retryDelay.ToString()).Write();
        }

        private void EnsureMaintenanceRunning() {
            if (!_runMaintenanceTasks || _maintenanceTask != null)
                return;

            lock (_lockObject) {
                if (_maintenanceTask != null)
                    return;

                Logger.Trace().Message($"Starting maintenance for {_queueName}.").Write();
                _maintenanceTask = Task.Run(() => DoMaintenanceWorkLoop(_queueDisposedCancellationTokenSource.Token));
            }
        }

        private void EnsureTopicSubscription() {
            if (_isSubscribed)
                return;

            lock (_lockObject) {
                if (_isSubscribed)
                    return;

                Logger.Trace().Message($"Subscribing to enqueue messages for {_queueName}.").Write();
                _subscriber.Subscribe(GetTopicName(), OnTopicMessage);
            }
        }

        public override async Task<QueueStats> GetQueueStatsAsync() {
            return new QueueStats {
                Queued = await _db.ListLengthAsync(QueueListName).AnyContext(),
                Working = await _db.ListLengthAsync(WorkListName).AnyContext(),
                Deadletter = await _db.ListLengthAsync(DeadListName).AnyContext(),
                Enqueued = _enqueuedCount,
                Dequeued = _dequeuedCount,
                Completed = _completedCount,
                Abandoned = _abandonedCount,
                Errors = _workerErrorCount,
                Timeouts = _workItemTimeoutCount
            };
        }

        private string QueueListName { get; set; }
        private string WorkListName { get; set; }
        private string WaitListName { get; set; }
        private string DeadListName { get; set; }

        private string GetPayloadKey(string id) {
            return String.Concat("q:", _queueName, ":", id);
        }

        private TimeSpan GetPayloadTtl() {
            var ttl = TimeSpan.Zero;
            for (int attempt = 1; attempt <= _retries + 1; attempt++)
                ttl = ttl.Add(GetRetryDelay(attempt));

            // minimum of 7 days for payload
            return TimeSpan.FromMilliseconds(Math.Max(ttl.TotalMilliseconds * 1.5, TimeSpan.FromDays(7).TotalMilliseconds));
        }

        private string GetAttemptsKey(string id) {
            return String.Concat("q:", _queueName, ":", id, ":attempts");
        }

        private TimeSpan GetAttemptsTtl() {
            return _payloadTtl;
        }

        private string GetEnqueuedTimeKey(string id) {
            return String.Concat("q:", _queueName, ":", id, ":enqueued");
        }

        private string GetDequeuedTimeKey(string id) {
            return String.Concat("q:", _queueName, ":", id, ":dequeued");
        }

        private TimeSpan GetDequeuedTimeTtl() {
            return TimeSpan.FromMilliseconds(Math.Max(_workItemTimeout.TotalMilliseconds * 1.5, TimeSpan.FromHours(1).TotalMilliseconds));
        }

        private string GetWaitTimeKey(string id) {
            return String.Concat("q:", _queueName, ":", id, ":wait");
        }

        private TimeSpan GetWaitTimeTtl() {
            return _payloadTtl;
        }

        private string GetTopicName() {
            return String.Concat("q:", _queueName, ":in");
        }

        public override async Task<string> EnqueueAsync(T data) {
            string id = Guid.NewGuid().ToString("N");
            Logger.Debug().Message($"Queue {_queueName} enqueue item: {id}").Write();
            if (!await OnEnqueuingAsync(data).AnyContext()) {
                Logger.Trace().Message($"Aborting enqueue item: {id}").Write();
                return null;
            }

            bool success = await _cache.AddAsync(GetPayloadKey(id), data, _payloadTtl).AnyContext();
            if (!success)
                throw new InvalidOperationException("Attempt to set payload failed.");

            await _db.ListLeftPushAsync(QueueListName, id).AnyContext();
            await _cache.SetAsync(GetEnqueuedTimeKey(id), DateTime.UtcNow.Ticks, _payloadTtl).AnyContext();

            // This should pulse the monitor.
            await _subscriber.PublishAsync(GetTopicName(), id).AnyContext();

            Interlocked.Increment(ref _enqueuedCount);
            await OnEnqueuedAsync(data, id).AnyContext();
            Logger.Trace().Message($"Enqueue done").Write();

            return id;
        }

        public override void StartWorking(Func<QueueEntry<T>, CancellationToken, Task> handler, bool autoComplete = false, CancellationToken cancellationToken = default(CancellationToken)) {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            var linkedCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(_queueDisposedCancellationTokenSource.Token, cancellationToken).Token;

            Task.Run(async () => {
                Logger.Trace().Message("WorkerLoop Start {0}", _queueName).Write();
                while (!linkedCancellationToken.IsCancellationRequested) {
                    Logger.Trace().Message("WorkerLoop Pass {0}", _queueName).Write();
                    QueueEntry<T> queueEntry = null;
                    try {
                        queueEntry = await DequeueAsync(cancellationToken: cancellationToken).AnyContext();
                    } catch (TimeoutException) { }

                    if (linkedCancellationToken.IsCancellationRequested || queueEntry == null)
                        continue;

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

                Logger.Trace().Message("Worker exiting: {0} Cancel Requested: {1}", _queueName, linkedCancellationToken.IsCancellationRequested).Write();
            }, linkedCancellationToken);
        }

        public override async Task<QueueEntry<T>> DequeueAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            Logger.Trace().Message($"Queue {_queueName} dequeuing item...").Write();
            EnsureMaintenanceRunning();
            EnsureTopicSubscription();
            var linkedCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(_queueDisposedCancellationTokenSource.Token, cancellationToken).Token;

            RedisValue value = await _db.ListRightPopLeftPushAsync(QueueListName, WorkListName).AnyContext();
            if (linkedCancellationToken.IsCancellationRequested && value.IsNullOrEmpty)
                return null;

            Logger.Trace().Message("Initial list value: {0}", (value.IsNullOrEmpty ? "<null>" : value.ToString())).Write();

            while (value.IsNullOrEmpty && !linkedCancellationToken.IsCancellationRequested) {
                Logger.Trace().Message("Waiting to dequeue item...").Write();

                var sw = Stopwatch.StartNew();
                try {
                    using (await _monitor.EnterAsync(cancellationToken))
                        await _monitor.WaitAsync(cancellationToken).AnyContext();
                } catch (TaskCanceledException) { }
                sw.Stop();
                Logger.Trace().Message("Waited for dequeue: {0}", sw.Elapsed.ToString()).Write();

                value = await _db.ListRightPopLeftPushAsync(QueueListName, WorkListName).AnyContext();
                Logger.Trace().Message("List value: {0}", (value.IsNullOrEmpty ? "<null>" : value.ToString())).Write();
            }

            if (value.IsNullOrEmpty)
                return null;

            await _cache.SetAsync(GetDequeuedTimeKey(value), DateTime.UtcNow.Ticks, GetDequeuedTimeTtl()).AnyContext();

            try {
                var payload = await _cache.GetAsync<T>(GetPayloadKey(value)).AnyContext();
                if (payload == null) {
                    Logger.Error().Message("Error getting queue payload: {0}", value).Write();
                    await _db.ListRemoveAsync(WorkListName, value).AnyContext();
                    return null;
                }

                var enqueuedTimeTicks = await _cache.GetAsync<long?>(GetEnqueuedTimeKey(value)).AnyContext() ?? 0;
                var attemptsValue = await _cache.GetAsync<int?>(GetAttemptsKey(value)).AnyContext() ?? -1;
                var entry = new QueueEntry<T>(value, payload, this, new DateTime(enqueuedTimeTicks, DateTimeKind.Utc), attemptsValue);
                Interlocked.Increment(ref _dequeuedCount);
                await OnDequeuedAsync(entry).AnyContext();

                Logger.Debug().Message("Dequeued item: {0}", value).Write();
                return entry;
            } catch (Exception ex) {
                Logger.Error().Exception(ex).Message("Error getting queue payload: {0}", value).Write();
                throw;
            }
        }

        public override async Task CompleteAsync(string id) {
            Logger.Debug().Message("Queue {0} complete item: {1}", _queueName, id).Write();

            var tasks = new List<Task>();
            var batch = _db.CreateBatch();
            tasks.Add(batch.ListRemoveAsync(WorkListName, id));
            tasks.Add(batch.KeyDeleteAsync(GetPayloadKey(id)));
            tasks.Add(batch.KeyDeleteAsync(GetAttemptsKey(id)));
            tasks.Add(batch.KeyDeleteAsync(GetEnqueuedTimeKey(id)));
            tasks.Add(batch.KeyDeleteAsync(GetDequeuedTimeKey(id)));
            tasks.Add(batch.KeyDeleteAsync(GetWaitTimeKey(id)));
            batch.Execute();

            await Task.WhenAll(tasks.ToArray()).AnyContext();

            Interlocked.Increment(ref _completedCount);
            await OnCompletedAsync(id).AnyContext();
            Logger.Trace().Message("Complete done: {0}", id).Write();
        }

        public override async Task AbandonAsync(string id) {
            Logger.Debug().Message($"Queue {_queueName}:{QueueId} abandon item: {id}").Write();
            var attemptsValue = await _cache.GetAsync<int?>(GetAttemptsKey(id)).AnyContext();
            int attempts = 1;
            if (attemptsValue.HasValue)
                attempts = attemptsValue.Value + 1;
            
            var retryDelay = GetRetryDelay(attempts);
            Logger.Trace().Message($"Item: {id} Retry attempts: {attempts} delay: {retryDelay} allowed: {_retries}").Write();
            if (attempts > _retries) {
                Logger.Trace().Message($"Exceeded retry limit moving to deadletter: {id}").Write();

                var tx = _db.CreateTransaction();
                tx.ListRemoveAsync(WorkListName, id);
                tx.ListLeftPushAsync(DeadListName, id);
                tx.KeyExpireAsync(GetPayloadKey(id), _deadLetterTtl);
                var success = await tx.ExecuteAsync().AnyContext();
                if (!success)
                    throw new Exception($"Unable to move item to wait list: {id}");

                await _cache.IncrementAsync(GetAttemptsKey(id), 1, GetAttemptsTtl()).AnyContext();
            } else if (retryDelay > TimeSpan.Zero) {
                Logger.Trace().Message($"Adding item to wait list for future retry: {id}").Write();
                await _cache.SetAsync(GetWaitTimeKey(id), DateTime.UtcNow.Add(retryDelay).Ticks, GetWaitTimeTtl()).AnyContext();
                await _cache.IncrementAsync(GetAttemptsKey(id), 1, GetAttemptsTtl()).AnyContext();

                var tx = _db.CreateTransaction();
                tx.ListRemoveAsync(WorkListName, id);
                tx.ListLeftPushAsync(WaitListName, id);
                var success = await tx.ExecuteAsync().AnyContext();
                if (!success)
                    throw new Exception($"Unable to move item to wait list: {id}");
            } else {
                Logger.Trace().Message($"Adding item back to queue for retry: {id}").Write();
                await _cache.IncrementAsync(GetAttemptsKey(id), 1, GetAttemptsTtl()).AnyContext();

                var tx = _db.CreateTransaction();
                tx.ListRemoveAsync(WorkListName, id);
                tx.ListLeftPushAsync(QueueListName, id);
                var success = await tx.ExecuteAsync().AnyContext();
                if (!success)
                    throw new Exception($"Unable to move item to queue list: {id}");
                
                // This should pulse the monitor.
                await _subscriber.PublishAsync(GetTopicName(), id).AnyContext();
            }

            Interlocked.Increment(ref _abandonedCount);
            await OnAbandonedAsync(id).AnyContext();
            Logger.Trace().Message($"Abandon complete: {id}").Write();
        }

        private TimeSpan GetRetryDelay(int attempts) {
            if (_retryDelay <= TimeSpan.Zero)
                return TimeSpan.Zero;

            int maxMultiplier = _retryMultipliers.Length > 0 ? _retryMultipliers.Last() : 1;
            int multiplier = attempts <= _retryMultipliers.Length ? _retryMultipliers[attempts - 1] : maxMultiplier;
            return TimeSpan.FromMilliseconds(_retryDelay.TotalMilliseconds * multiplier);
        }

        public override Task<IEnumerable<T>> GetDeadletterItemsAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            throw new NotImplementedException();
        }

        public override async Task DeleteQueueAsync() {
            Logger.Trace().Message("Deleting queue: {0}", _queueName).Write();
            await DeleteListAsync(QueueListName).AnyContext();
            await DeleteListAsync(WorkListName).AnyContext();
            await DeleteListAsync(WaitListName).AnyContext();
            await DeleteListAsync(DeadListName).AnyContext();
            _enqueuedCount = 0;
            _dequeuedCount = 0;
            _completedCount = 0;
            _abandonedCount = 0;
            _workerErrorCount = 0;
        }

        private async Task DeleteListAsync(string name) {
            // TODO Look into running this as a batch query.
            var itemIds = await _db.ListRangeAsync(name).AnyContext();
            foreach (var id in itemIds) {
                await _db.KeyDeleteAsync(GetPayloadKey(id)).AnyContext();
                await _db.KeyDeleteAsync(GetAttemptsKey(id)).AnyContext();
                await _db.KeyDeleteAsync(GetEnqueuedTimeKey(id)).AnyContext();
                await _db.KeyDeleteAsync(GetDequeuedTimeKey(id)).AnyContext();
                await _db.KeyDeleteAsync(GetWaitTimeKey(id)).AnyContext();
            }

            await _db.KeyDeleteAsync(name).AnyContext();
        }

        private async Task TrimDeadletterItemsAsync(int maxItems) {
            // TODO Look into running this as a batch query.
            var itemIds = (await _db.ListRangeAsync(DeadListName).AnyContext()).Skip(maxItems);
            foreach (var id in itemIds) {
                await _db.KeyDeleteAsync(GetPayloadKey(id)).AnyContext();
                await _db.KeyDeleteAsync(GetAttemptsKey(id)).AnyContext();
                await _db.KeyDeleteAsync(GetEnqueuedTimeKey(id)).AnyContext();
                await _db.KeyDeleteAsync(GetDequeuedTimeKey(id)).AnyContext();
                await _db.KeyDeleteAsync(GetWaitTimeKey(id)).AnyContext();
                await _db.ListRemoveAsync(QueueListName, id).AnyContext();
                await _db.ListRemoveAsync(WorkListName, id).AnyContext();
                await _db.ListRemoveAsync(WaitListName, id).AnyContext();
                await _db.ListRemoveAsync(DeadListName, id).AnyContext();
            }
        }

        private async void OnTopicMessage(RedisChannel redisChannel, RedisValue redisValue) {
            Logger.Trace().Message("Queue OnMessage {0}: {1}", _queueName, redisValue).Write();
            using (await _monitor.EnterAsync())
                _monitor.Pulse();
        }

        internal async Task DoMaintenanceWorkAsync() {
            Logger.Trace().Message("DoMaintenance: Name={0} Id={1}", _queueName, QueueId).Write();

            try {
                var workIds = await _db.ListRangeAsync(WorkListName).AnyContext();
                foreach (var workId in workIds) {
                    var dequeuedTimeTicks = await _cache.GetAsync<long?>(GetDequeuedTimeKey(workId)).AnyContext();

                    // dequeue time should be set, use current time
                    if (!dequeuedTimeTicks.HasValue) {
                        await _cache.SetAsync(GetDequeuedTimeKey(workId), DateTime.UtcNow.Ticks, GetDequeuedTimeTtl()).AnyContext();
                        continue;
                    }

                    var dequeuedTime = new DateTime(dequeuedTimeTicks.Value);
                    Logger.Trace().Message("Dequeue time {0}", dequeuedTime).Write();
                    if (DateTime.UtcNow.Subtract(dequeuedTime) <= _workItemTimeout)
                        continue;

                    Logger.Trace().Message("Auto abandon item {0}", workId).Write();
                    await AbandonAsync(workId).AnyContext();
                    Interlocked.Increment(ref _workItemTimeoutCount);
                }
            } catch (Exception ex) {
                Logger.Error().Exception(ex).Message("Error checking for work item timeouts: {0}", ex.Message).Write();
            }

            try {
                var waitIds = await _db.ListRangeAsync(WaitListName).AnyContext();
                foreach (var waitId in waitIds) {
                    var waitTimeTicks = await _cache.GetAsync<long?>(GetWaitTimeKey(waitId)).AnyContext();
                    Logger.Trace().Message("Wait time: {0}", waitTimeTicks).Write();

                    if (waitTimeTicks.HasValue && waitTimeTicks.Value > DateTime.UtcNow.Ticks)
                        continue;

                    Logger.Trace().Message("Getting retry lock").Write();

                    Logger.Trace().Message("Adding item back to queue for retry: {0}", waitId).Write();
                    var tx = _db.CreateTransaction();
                    tx.ListRemoveAsync(WaitListName, waitId);
                    tx.ListLeftPushAsync(QueueListName, waitId);
                    var success = await tx.ExecuteAsync().AnyContext();
                    if (!success)
                        throw new Exception("Unable to move item to queue list.");

                    await _db.KeyDeleteAsync(GetWaitTimeKey(waitId)).AnyContext();
                    await _subscriber.PublishAsync(GetTopicName(), waitId).AnyContext();
                }
            } catch (Exception ex) {
                Logger.Error().Exception(ex).Message("Error adding items back to the queue after the retry delay: {0}", ex.Message).Write();
            }

            try {
                await TrimDeadletterItemsAsync(_deadLetterMaxItems).AnyContext();
            } catch (Exception ex) {
                Logger.Error().Exception(ex).Message("Error trimming deadletter items: {0}", ex.Message).Write();
            }
        }

        private async Task DoMaintenanceWorkLoop(CancellationToken token) {
            while (!token.IsCancellationRequested) {
                Logger.Trace().Message("Requesting Maintenance Lock: Name={0} Id={1}", _queueName, QueueId).Write();
                await _maintenanceLockProvider.TryUsingLockAsync(_queueName + "-maintenance", async () => await DoMaintenanceWorkAsync().AnyContext(), acquireTimeout: TimeSpan.FromSeconds(30));
            }
        }

        public override async void Dispose() {
            Logger.Trace().Message("Queue {0} dispose", _queueName).Write();

            await _subscriber.UnsubscribeAllAsync().AnyContext();
            _queueDisposedCancellationTokenSource?.Cancel();

            base.Dispose();
        }
    }
}
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Extensions;
using Foundatio.Lock;
using Foundatio.Metrics;
using Foundatio.Serializer;
using Foundatio.Utility;
using Nito.AsyncEx;
using Foundatio.Logging;
using StackExchange.Redis;

namespace Foundatio.Queues {
    public class RedisQueue<T> : QueueBase<T> where T: class {
        private readonly string _queueName;
        protected readonly IDatabase _db;
        protected readonly ISubscriber _subscriber;
        protected readonly RedisCacheClient _cache;
        private Action<QueueEntry<T>> _workerAction;
        private bool _workerAutoComplete;
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
        private CancellationTokenSource _workerCancellationTokenSource;
        private readonly CancellationTokenSource _queueDisposedCancellationTokenSource;
        private readonly AsyncAutoResetEvent _autoEvent = new AsyncAutoResetEvent(false);
        protected readonly IMetricsClient _metrics;
        private readonly Timer _maintenanceTimer;
        protected readonly ILockProvider _maintenanceLockProvider;

        public RedisQueue(ConnectionMultiplexer connection, ISerializer serializer = null, string queueName = null, int retries = 2, TimeSpan? retryDelay = null, int[] retryMultipliers = null,
            TimeSpan? workItemTimeout = null, TimeSpan? deadLetterTimeToLive = null, int deadLetterMaxItems = 100, bool runMaintenanceTasks = true, IEnumerable<IQueueBehavior<T>> behaviors = null)
            : base(serializer, behaviors)
        {
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

            _subscriber = connection.GetSubscriber();
            _subscriber.Subscribe(GetTopicName(), OnTopicMessage);

            if (runMaintenanceTasks) {
                _queueDisposedCancellationTokenSource = new CancellationTokenSource();
                // min is 1 second, max is 1 minute
                TimeSpan interval = _workItemTimeout > TimeSpan.FromSeconds(1) ? _workItemTimeout.Min(TimeSpan.FromMinutes(1)) : TimeSpan.FromSeconds(1);
                _maintenanceLockProvider = new ThrottlingLockProvider(_cache, 1, interval);
                _maintenanceTimer = new Timer(t => DoMaintenanceWorkAsync().Wait(), null, TimeSpan.FromMilliseconds(250), interval);
            }

            Logger.Trace().Message("Queue {0} created. Retries: {1} Retry Delay: {2}", QueueId, _retries, _retryDelay.ToString()).Write();
        }

        public override async Task<QueueStats> GetQueueStatsAsync() {
            return new QueueStats {
                Queued = await _db.ListLengthAsync(QueueListName),
                Working = await _db.ListLengthAsync(WorkListName),
                Deadletter = await _db.ListLengthAsync(DeadListName),
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

        private string GetEnqueuedTimeKey(string id)
        {
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
            Logger.Debug().Message("Queue {0} enqueue item: {1}", _queueName, id).Write();
            if (!OnEnqueuing(data))
                return null;

            bool success = _cache.Add(GetPayloadKey(id), data, _payloadTtl);
            if (!success)
                throw new InvalidOperationException("Attempt to set payload failed.");

            await _db.ListLeftPushAsync(QueueListName, id);
            await _subscriber.PublishAsync(GetTopicName(), id);
            Interlocked.Increment(ref _enqueuedCount);
            OnEnqueued(data, id);
            Logger.Trace().Message("Enqueue done").Write();

            return id;
        }

        public override Task StartWorkingAsync(Action<QueueEntry<T>> handler, bool autoComplete = false, CancellationToken cancellationToken = default(CancellationToken)) {
            if (handler == null)
                throw new ArgumentNullException("handler");

            Logger.Trace().Message("Queue {0} start working", _queueName).Write();
            _workerAction = handler;
            _workerAutoComplete = autoComplete;

            if (_workerCancellationTokenSource != null)
                return Task.FromResult(0);

            _workerCancellationTokenSource = new CancellationTokenSource();
            return Task.Factory.StartNew(() => WorkerLoop(_workerCancellationTokenSource.Token));
        }

        public override async Task StopWorkingAsync() {
            Logger.Trace().Message("Queue {0} stop working", _queueName).Write();
            _workerAction = null;
            await _subscriber.UnsubscribeAllAsync();

            _workerCancellationTokenSource?.Cancel();
            _autoEvent.Set();
        }

        public override async Task<QueueEntry<T>> DequeueAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default(CancellationToken)) {
            Logger.Trace().Message("Queue {0} dequeuing item (timeout: {1})...", _queueName, timeout?.ToString() ?? "(none)").Write();
            if (!timeout.HasValue)
                timeout = TimeSpan.FromSeconds(30);

            RedisValue value = await _db.ListRightPopLeftPushAsync(QueueListName, WorkListName);
            Logger.Trace().Message("Initial list value: {0}", (value.IsNullOrEmpty ? "<null>" : value.ToString())).Write();

            DateTime started = DateTime.UtcNow;
            while (value.IsNullOrEmpty && timeout > TimeSpan.Zero && DateTime.UtcNow.Subtract(started) < timeout) {
                Logger.Trace().Message("Waiting to dequeue item...").Write();

                // Wait for timeout or signal or dispose
                Task.WaitAny(Task.Delay(timeout.Value, cancellationToken), _autoEvent.WaitAsync(_queueDisposedCancellationTokenSource.Token));
                if (_queueDisposedCancellationTokenSource.IsCancellationRequested)
                    return null;

                value = await _db.ListRightPopLeftPushAsync(QueueListName, WorkListName);
                Logger.Trace().Message("List value: {0}", (value.IsNullOrEmpty ? "<null>" : value.ToString())).Write();
            }

            if (value.IsNullOrEmpty)
                return null;

            _cache.Set(GetDequeuedTimeKey(value), DateTime.UtcNow.Ticks, GetDequeuedTimeTtl());

            try {
                var payload = _cache.Get<T>(GetPayloadKey(value));
                if (payload == null) {
                    Logger.Error().Message("Error getting queue payload: {0}", value).Write();
                    _db.ListRemove(WorkListName, value);
                    return null;
                }

                // TODO: Fix params
                var entry = new QueueEntry<T>(value, payload, this, DateTime.MinValue, 1);
                Interlocked.Increment(ref _dequeuedCount);
                OnDequeued(entry);

                Logger.Debug().Message("Dequeued item: {0}", value).Write();
                return entry;
            } catch (Exception ex) {
                Logger.Error().Exception(ex).Message("Error getting queue payload: {0}", value).Write();
                throw;
            }
        }

        public override Task CompleteAsync(IQueueEntryMetadata entry) {
            Logger.Debug().Message("Queue {0} complete item: {1}", _queueName, entry.Id).Write();
            var batch = _db.CreateBatch();
            batch.ListRemoveAsync(WorkListName, entry.Id);
            batch.KeyDeleteAsync(GetPayloadKey(entry.Id));
            batch.KeyDeleteAsync(GetAttemptsKey(entry.Id));
            batch.KeyDeleteAsync(GetDequeuedTimeKey(entry.Id));
            batch.KeyDeleteAsync(GetWaitTimeKey(entry.Id));
            batch.Execute();
            Interlocked.Increment(ref _completedCount);
            OnCompleted(entry);
            Logger.Trace().Message("Complete done: {0}", entry.Id).Write();
            return Task.FromResult(0);
        }

        public override async Task AbandonAsync(IQueueEntryMetadata entry) {
            Logger.Debug().Message("Queue {0} abandon item: {1}", _queueName + ":" + QueueId, entry.Id).Write();
            var attemptsValue = _cache.Get<int?>(GetAttemptsKey(entry.Id));
            int attempts = 1;
            if (attemptsValue.HasValue)
                attempts = attemptsValue.Value + 1;
            Logger.Trace().Message("Item attempts: {0}", attempts).Write();

            var retryDelay = GetRetryDelay(attempts);
            Logger.Trace().Message("Retry attempts: {0} delay: {1} allowed: {2}", attempts, retryDelay.ToString(), _retries).Write();
            if (attempts > _retries) {
                Logger.Trace().Message("Exceeded retry limit moving to deadletter: {0}", entry.Id).Write();
                await _db.ListRemoveAsync(WorkListName, entry.Id);
                await _db.ListLeftPushAsync(DeadListName, entry.Id);
                await _db.KeyExpireAsync(GetPayloadKey(entry.Id), _deadLetterTtl);
                _cache.Increment(GetAttemptsKey(entry.Id), 1, GetAttemptsTtl());
            } else if (retryDelay > TimeSpan.Zero) {
                Logger.Trace().Message("Adding item to wait list for future retry: {0}", entry.Id).Write();
                var tx = _db.CreateTransaction();
                await tx.ListRemoveAsync(WorkListName, entry.Id);
                await tx.ListLeftPushAsync(WaitListName, entry.Id);
                var success = await tx.ExecuteAsync();
                if (!success)
                    throw new Exception("Unable to move item to wait list.");

                _cache.Set(GetWaitTimeKey(entry.Id), DateTime.UtcNow.Add(retryDelay).Ticks, GetWaitTimeTtl());
                _cache.Increment(GetAttemptsKey(entry.Id), 1, GetAttemptsTtl());
            } else {
                Logger.Trace().Message("Adding item back to queue for retry: {0}", entry.Id).Write();
                var tx = _db.CreateTransaction();
                await tx.ListRemoveAsync(WorkListName, entry.Id);
                await tx.ListLeftPushAsync(QueueListName, entry.Id);
                var success = await tx.ExecuteAsync();
                if (!success)
                    throw new Exception("Unable to move item to queue list.");

                _cache.Increment(GetAttemptsKey(entry.Id), 1, GetAttemptsTtl());
                await _subscriber.PublishAsync(GetTopicName(), entry.Id);
            }

            Interlocked.Increment(ref _abandonedCount);
            OnAbandoned(entry);
            Logger.Trace().Message("Abondon complete: {0}", entry.Id).Write();
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
            await DeleteListAsync(QueueListName);
            await DeleteListAsync(WorkListName);
            await DeleteListAsync(WaitListName);
            await DeleteListAsync(DeadListName);
            _enqueuedCount = 0;
            _dequeuedCount = 0;
            _completedCount = 0;
            _abandonedCount = 0;
            _workerErrorCount = 0;
        }

        private async Task DeleteListAsync(string name) {
            // TODO Look into running this as a batch query.
            var itemIds = await _db.ListRangeAsync(name);
            foreach (var id in itemIds) {
                await _db.KeyDeleteAsync(GetPayloadKey(id));
                await _db.KeyDeleteAsync(GetAttemptsKey(id));
                await _db.KeyDeleteAsync(GetDequeuedTimeKey(id));
                await _db.KeyDeleteAsync(GetWaitTimeKey(id));
            }

            await _db.KeyDeleteAsync(name);
        }

        private async Task TrimDeadletterItemsAsync(int maxItems) {
            // TODO Look into running this as a batch query.
            var itemIds = (await _db.ListRangeAsync(DeadListName)).Skip(maxItems);
            foreach (var id in itemIds) {
                await _db.KeyDeleteAsync(GetPayloadKey(id));
                await _db.KeyDeleteAsync(GetAttemptsKey(id));
                await _db.KeyDeleteAsync(GetDequeuedTimeKey(id));
                await _db.KeyDeleteAsync(GetWaitTimeKey(id));
                await _db.ListRemoveAsync(QueueListName, id);
                await _db.ListRemoveAsync(WorkListName, id);
                await _db.ListRemoveAsync(WaitListName, id);
                await _db.ListRemoveAsync(DeadListName, id);
            }
        }

        private void OnTopicMessage(RedisChannel redisChannel, RedisValue redisValue) {
            Logger.Trace().Message("Queue OnMessage {0}: {1}", _queueName, redisValue).Write();
            _autoEvent.Set();
        }

        private async Task WorkerLoop(CancellationToken token) {
            Logger.Trace().Message("WorkerLoop Start {0}", _queueName).Write();
            while (!token.IsCancellationRequested && _workerAction != null) {
                Logger.Trace().Message("WorkerLoop Pass {0}", _queueName).Write();
                QueueEntry<T> queueEntry = null;
                try {
                    queueEntry = await DequeueAsync();
                } catch (TimeoutException) { }

                if (token.IsCancellationRequested || queueEntry == null)
                    continue;

                try {
                    _workerAction(queueEntry);
                    if (_workerAutoComplete)
                        await queueEntry.CompleteAsync();
                } catch (Exception ex) {
                    Logger.Error().Exception(ex).Message("Worker error: {0}", ex.Message).Write();
                    await queueEntry.AbandonAsync();
                    Interlocked.Increment(ref _workerErrorCount);
                }
            }

            Logger.Trace().Message("Worker exiting: {0} Cancel Requested: {1}", _queueName, token.IsCancellationRequested).Write();
        }

        internal async Task DoMaintenanceWorkAsync() {
            Logger.Trace().Message("DoMaintenance: Name={0} Id={1}", _queueName, QueueId).Write();

            try {
                var workIds = await _db.ListRangeAsync(WorkListName);
                foreach (var workId in workIds) {
                    var dequeuedTimeTicks = _cache.Get<long?>(GetDequeuedTimeKey(workId));

                    // dequeue time should be set, use current time
                    if (!dequeuedTimeTicks.HasValue) {
                        _cache.Set(GetDequeuedTimeKey(workId), DateTime.UtcNow.Ticks, GetDequeuedTimeTtl());
                        continue;
                    }

                    var dequeuedTime = new DateTime(dequeuedTimeTicks.Value);
                    Logger.Trace().Message("Dequeue time {0}", dequeuedTime).Write();
                    if (DateTime.UtcNow.Subtract(dequeuedTime) <= _workItemTimeout)
                        continue;

                    Logger.Trace().Message("Auto abandon item {0}", workId).Write();
                    // TODO: Fix parameters
                    await AbandonAsync(new QueueEntry<T>(workId, null, this, DateTime.MinValue, 1));
                    Interlocked.Increment(ref _workItemTimeoutCount);
                }
            } catch (Exception ex) {
                Logger.Error().Exception(ex).Message("Error checking for work item timeouts: {0}", ex.Message).Write();
            }

            try {
                var waitIds = _db.ListRange(WaitListName);
                foreach (var waitId in waitIds) {
                    var waitTimeTicks = _cache.Get<long?>(GetWaitTimeKey(waitId));
                    Logger.Trace().Message("Wait time: {0}", waitTimeTicks).Write();

                    if (waitTimeTicks.HasValue && waitTimeTicks.Value > DateTime.UtcNow.Ticks)
                        continue;

                    Logger.Trace().Message("Getting retry lock").Write();

                    Logger.Trace().Message("Adding item back to queue for retry: {0}", waitId).Write();
                    var tx = _db.CreateTransaction();
#pragma warning disable 4014
                    await tx.ListRemoveAsync(WaitListName, waitId);
                    await tx.ListLeftPushAsync(QueueListName, waitId);
#pragma warning restore 4014
                    var success = await tx.ExecuteAsync();
                    if (!success)
                        throw new Exception("Unable to move item to queue list.");

                    await _db.KeyDeleteAsync(GetWaitTimeKey(waitId));
                    await _subscriber.PublishAsync(GetTopicName(), waitId);
                }
            } catch (Exception ex) {
                Logger.Error().Exception(ex).Message("Error adding items back to the queue after the retry delay: {0}", ex.Message).Write();
            }

            try {
               await TrimDeadletterItemsAsync(_deadLetterMaxItems);
            } catch (Exception ex) {
                Logger.Error().Exception(ex).Message("Error trimming deadletter items: {0}", ex.Message).Write();
            }
        }

        private async Task DoMaintenanceWorkAsync(object state) {
            Logger.Trace().Message("Requesting Maintenance Lock: Name={0} Id={1}", _queueName, QueueId).Write();
            _maintenanceLockProvider.TryUsingLock(_queueName + "-maintenance", async () => await DoMaintenanceWorkAsync(), acquireTimeout: TimeSpan.Zero);
        }

        public override void Dispose() {
            Logger.Trace().Message("Queue {0} dispose", _queueName).Write();
            StopWorkingAsync().Wait();
            _queueDisposedCancellationTokenSource?.Cancel();
            _maintenanceTimer?.Dispose();
        }
    }
}
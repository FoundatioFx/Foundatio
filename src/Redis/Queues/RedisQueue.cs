using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Extensions;
using Foundatio.Lock;
using Foundatio.Logging;
using Foundatio.Serializer;
using Nito.AsyncEx;
using Foundatio.Utility;
using StackExchange.Redis;
#pragma warning disable 4014

namespace Foundatio.Queues {
    public class RedisQueue<T> : QueueBase<T> where T : class {
        private readonly string _queueName;
        protected readonly ConnectionMultiplexer _connectionMultiplexer;
        protected readonly ISubscriber _subscriber;
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
        protected readonly AsyncLock _lock = new AsyncLock();
        private bool _isSubscribed;

        public RedisQueue(ConnectionMultiplexer connection, ISerializer serializer = null, string queueName = null, int retries = 2, TimeSpan? retryDelay = null, int[] retryMultipliers = null,
            TimeSpan? workItemTimeout = null, TimeSpan? deadLetterTimeToLive = null, int deadLetterMaxItems = 100, bool runMaintenanceTasks = true, IEnumerable<IQueueBehavior<T>> behaviors = null, ILoggerFactory loggerFactory = null)
            : base(serializer, behaviors, loggerFactory) {
            _connectionMultiplexer = connection;
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

            _logger.Trace("Queue {0} created. Retries: {1} Retry Delay: {2}", QueueId, _retries, _retryDelay.ToString());
        }

        private async Task EnsureMaintenanceRunningAsync() {
            if (!_runMaintenanceTasks || _maintenanceTask != null)
                return;

            using (await  _lock.LockAsync()) {
                if (_maintenanceTask != null)
                    return;

                _logger.Trace("Starting maintenance for {_queueName}.", _queueName);
                _maintenanceTask = Task.Run(() => DoMaintenanceWorkLoop(_queueDisposedCancellationTokenSource.Token));
            }
        }

        private async Task EnsureTopicSubscriptionAsync() {
            if (_isSubscribed)
                return;

            using (await _lock.LockAsync()) {
                if (_isSubscribed)
                    return;

                _isSubscribed = true;
                _logger.Trace("Subscribing to enqueue messages for {_queueName}.", _queueName);
                await _subscriber.SubscribeAsync(GetTopicName(), async (channel, value) => await OnTopicMessage(channel, value).AnyContext()).AnyContext();
            }
        }

        public override async Task<QueueStats> GetQueueStatsAsync() {
            return new QueueStats {
                Queued = await Database.ListLengthAsync(QueueListName).AnyContext(),
                Working = await Database.ListLengthAsync(WorkListName).AnyContext(),
                Deadletter = await Database.ListLengthAsync(DeadListName).AnyContext(),
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

        private IDatabase Database => _connectionMultiplexer.GetDatabase();

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
            _logger.Debug("Queue {_queueName} enqueue item: {id}", _queueName, id);

            if (!await OnEnqueuingAsync(data).AnyContext()) {
                _logger.Trace("Aborting enqueue item: {id}", id);
                return null;
            }

            bool success = await _cache.AddAsync(GetPayloadKey(id), data, _payloadTtl).AnyContext();
            if (!success)
                throw new InvalidOperationException("Attempt to set payload failed.");

            await Database.ListLeftPushAsync(QueueListName, id).AnyContext();
            await _cache.SetAsync(GetEnqueuedTimeKey(id), DateTime.UtcNow.Ticks, _payloadTtl).AnyContext();

            // This should pulse the monitor.
            await _subscriber.PublishAsync(GetTopicName(), id).AnyContext();

            Interlocked.Increment(ref _enqueuedCount);
            var entry = new QueueEntry<T>(id, data, this, DateTime.UtcNow, 0);
            await OnEnqueuedAsync(entry).AnyContext();

            _logger.Trace("Enqueue done");

            return id;
        }

        public override void StartWorking(Func<IQueueEntry<T>, CancellationToken, Task> handler, bool autoComplete = false, CancellationToken cancellationToken = default(CancellationToken)) {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            var linkedCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(_queueDisposedCancellationTokenSource.Token, cancellationToken).Token;

            Task.Run(async () => {
                _logger.Trace("WorkerLoop Start {_queueName}", _queueName);

                while (!linkedCancellationToken.IsCancellationRequested) {
                    _logger.Trace("WorkerLoop Pass {_queueName}", _queueName);

                    IQueueEntry<T> queueEntry = null;
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
                        _logger.Error(ex, "Worker error: {0}", ex.Message);
                        await queueEntry.AbandonAsync().AnyContext();
                        Interlocked.Increment(ref _workerErrorCount);
                    }
                }

                _logger.Trace("Worker exiting: {0} Cancel Requested: {1}", _queueName, linkedCancellationToken.IsCancellationRequested);
            }, linkedCancellationToken);
        }

        public override async Task<IQueueEntry<T>> DequeueAsync(CancellationToken cancellationToken) {
            _logger.Trace("Queue {_queueName} dequeuing item...", _queueName);

            await EnsureMaintenanceRunningAsync().AnyContext();
            await EnsureTopicSubscriptionAsync().AnyContext();
            var linkedCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(_queueDisposedCancellationTokenSource.Token, cancellationToken).Token;
            
            RedisValue value = await GetRedisValueAsync(linkedCancellationToken).AnyContext();
            if (linkedCancellationToken.IsCancellationRequested && value.IsNullOrEmpty)
                return null;

            _logger.Trace("Initial list value: {0}", (value.IsNullOrEmpty ? "<null>" : value.ToString()));

            while (value.IsNullOrEmpty && !linkedCancellationToken.IsCancellationRequested) {
                _logger.Trace("Waiting to dequeue item...");
                var sw = Stopwatch.StartNew();

                try {
                    using (await _monitor.EnterAsync(cancellationToken))
                        await _monitor.WaitAsync(cancellationToken).AnyContext();
                } catch (TaskCanceledException) { }

                sw.Stop();
                _logger.Trace("Waited for dequeue: {0}", sw.Elapsed.ToString());

                value = await GetRedisValueAsync(linkedCancellationToken).AnyContext();

                _logger.Trace("List value: {0}", (value.IsNullOrEmpty ? "<null>" : value.ToString()));
            }

            if (value.IsNullOrEmpty)
                return null;

            await _cache.SetAsync(GetDequeuedTimeKey(value), DateTime.UtcNow.Ticks, GetDequeuedTimeTtl()).AnyContext();

            try {
                var entry = await GetQueueEntry(value).AnyContext();
                if (entry == null)
                    return null;

                Interlocked.Increment(ref _dequeuedCount);
                await OnDequeuedAsync(entry).AnyContext();

                _logger.Debug("Dequeued item: {0}", value);

                return entry;
            } catch (Exception ex) {
                _logger.Error(ex, "Error getting queue payload: {0}", value);
                throw;
            }
        }

        public override async Task RenewLockAsync(IQueueEntry<T> entry) {
            await OnLockRenewedAsync(entry).AnyContext();
        }

        private async Task<QueueEntry<T>> GetQueueEntry(string workId) {
            var payload = await _cache.GetAsync<T>(GetPayloadKey(workId)).AnyContext();
            if (payload.IsNull) {
                _logger.Error("Error getting queue payload: {0}", workId);
                await Database.ListRemoveAsync(WorkListName, workId).AnyContext();
                return null;
            }

            var enqueuedTimeTicks = await _cache.GetAsync<long>(GetEnqueuedTimeKey(workId), 0).AnyContext();
            var attemptsValue = await _cache.GetAsync<int>(GetAttemptsKey(workId), 1).AnyContext();

            return new QueueEntry<T>(workId, payload.Value, this, new DateTime(enqueuedTimeTicks, DateTimeKind.Utc), attemptsValue);
        }

        private async Task<RedisValue> GetRedisValueAsync(CancellationToken linkedCancellationToken) {
            try {
                return await Run.WithRetriesAsync(() => Database.ListRightPopLeftPushAsync(QueueListName, WorkListName), 3, TimeSpan.FromMilliseconds(100), linkedCancellationToken).AnyContext();
            } catch (TaskCanceledException) {
                return RedisValue.Null;
            }
        }

        public override async Task CompleteAsync(IQueueEntry<T> entry) {
            _logger.Debug("Queue {0} complete item: {1}", _queueName, entry.Id);

            var tasks = new List<Task> {
                Database.ListRemoveAsync(WorkListName, entry.Id),
                Database.KeyDeleteAsync(GetPayloadKey(entry.Id)),
                Database.KeyDeleteAsync(GetAttemptsKey(entry.Id)),
                Database.KeyDeleteAsync(GetEnqueuedTimeKey(entry.Id)),
                Database.KeyDeleteAsync(GetDequeuedTimeKey(entry.Id)),
                Database.KeyDeleteAsync(GetWaitTimeKey(entry.Id))
            };

            await Task.WhenAll(tasks).AnyContext();

            Interlocked.Increment(ref _completedCount);
            await OnCompletedAsync(entry).AnyContext();

            _logger.Trace("Complete done: {0}", entry.Id);
        }

        public override async Task AbandonAsync(IQueueEntry<T> entry) {
            _logger.Debug("Queue {_queueName}:{QueueId} abandon item: {entryId}", _queueName, QueueId, entry.Id);

            var attemptsCachedValue = await _cache.GetAsync<int>(GetAttemptsKey(entry.Id)).AnyContext();
            int attempts = 1;
            if (attemptsCachedValue.HasValue)
                attempts = attemptsCachedValue.Value + 1;
            
            var retryDelay = GetRetryDelay(attempts);
            _logger.Trace("Item: {entryId} Retry attempts: {attempts} delay: {retryDelay} allowed: {_retries}", entry.Id, attempts, retryDelay, _retries);

            if (attempts > _retries) {
                _logger.Trace("Exceeded retry limit moving to deadletter: {entryId}", entry.Id);

                var tx = Database.CreateTransaction();
                tx.ListRemoveAsync(WorkListName, entry.Id);
                tx.ListLeftPushAsync(DeadListName, entry.Id);
                tx.KeyExpireAsync(GetPayloadKey(entry.Id), _deadLetterTtl);
                var success = await tx.ExecuteAsync().AnyContext();
                if (!success)
                    throw new Exception($"Unable to move item to wait list: {entry.Id}");

                await _cache.IncrementAsync(GetAttemptsKey(entry.Id), 1, GetAttemptsTtl()).AnyContext();
            } else if (retryDelay > TimeSpan.Zero) {
                _logger.Trace("Adding item to wait list for future retry: {entryId}", entry.Id);

                await _cache.SetAsync(GetWaitTimeKey(entry.Id), DateTime.UtcNow.Add(retryDelay).Ticks, GetWaitTimeTtl()).AnyContext();
                await _cache.IncrementAsync(GetAttemptsKey(entry.Id), 1, GetAttemptsTtl()).AnyContext();

                var tx = Database.CreateTransaction();
                tx.ListRemoveAsync(WorkListName, entry.Id);
                tx.ListLeftPushAsync(WaitListName, entry.Id);
                var success = await tx.ExecuteAsync().AnyContext();
                if (!success)
                    throw new Exception($"Unable to move item to wait list: {entry.Id}");
            } else {
                _logger.Trace("Adding item back to queue for retry: {entryId}", entry.Id);

                await _cache.IncrementAsync(GetAttemptsKey(entry.Id), 1, GetAttemptsTtl()).AnyContext();

                var tx = Database.CreateTransaction();
                tx.ListRemoveAsync(WorkListName, entry.Id);
                tx.ListLeftPushAsync(QueueListName, entry.Id);
                var success = await tx.ExecuteAsync().AnyContext();
                if (!success)
                    throw new Exception($"Unable to move item to queue list: {entry.Id}");
                
                // This should pulse the monitor.
                await _subscriber.PublishAsync(GetTopicName(), entry.Id).AnyContext();
            }

            Interlocked.Increment(ref _abandonedCount);
            await OnAbandonedAsync(entry).AnyContext();
            _logger.Trace("Abandon complete: {entryId}", entry.Id);
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
            _logger.Trace("Deleting queue: {0}", _queueName);
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
            var itemIds = await Database.ListRangeAsync(name).AnyContext();
            foreach (var id in itemIds) {
                var tasks = new List<Task> {
                    Database.KeyDeleteAsync(GetPayloadKey(id)),
                    Database.KeyDeleteAsync(GetAttemptsKey(id)),
                    Database.KeyDeleteAsync(GetEnqueuedTimeKey(id)),
                    Database.KeyDeleteAsync(GetDequeuedTimeKey(id)),
                    Database.KeyDeleteAsync(GetWaitTimeKey(id))
                };
                
                await Task.WhenAll(tasks).AnyContext();
            }

            await Database.KeyDeleteAsync(name).AnyContext();
        }

        private async Task TrimDeadletterItemsAsync(int maxItems) {
            var itemIds = (await Database.ListRangeAsync(DeadListName).AnyContext()).Skip(maxItems);
            foreach (var id in itemIds) {
                var tasks = new List<Task> {
                    Database.KeyDeleteAsync(GetPayloadKey(id)),
                    Database.KeyDeleteAsync(GetAttemptsKey(id)),
                    Database.KeyDeleteAsync(GetEnqueuedTimeKey(id)),
                    Database.KeyDeleteAsync(GetDequeuedTimeKey(id)),
                    Database.KeyDeleteAsync(GetWaitTimeKey(id)),
                    Database.ListRemoveAsync(QueueListName, id),
                    Database.ListRemoveAsync(WorkListName, id),
                    Database.ListRemoveAsync(WaitListName, id),
                    Database.ListRemoveAsync(DeadListName, id)
                };

                await Task.WhenAll(tasks).AnyContext();
            }
        }

        private async Task OnTopicMessage(RedisChannel redisChannel, RedisValue redisValue) {
            _logger.Trace("Queue OnMessage {0}: {1}", _queueName, redisValue);

            using (await _monitor.EnterAsync())
                _monitor.Pulse();
        }

        internal async Task DoMaintenanceWorkAsync() {
            _logger.Trace("DoMaintenance: Name={0} Id={1}", _queueName, QueueId);

            try {
                var workIds = await Database.ListRangeAsync(WorkListName).AnyContext();
                foreach (var workId in workIds) {
                    var dequeuedTimeTicks = await _cache.GetAsync<long>(GetDequeuedTimeKey(workId)).AnyContext();

                    // dequeue time should be set, use current time
                    if (!dequeuedTimeTicks.HasValue) {
                        await _cache.SetAsync(GetDequeuedTimeKey(workId), DateTime.UtcNow.Ticks, GetDequeuedTimeTtl()).AnyContext();
                        continue;
                    }

                    var dequeuedTime = new DateTime(dequeuedTimeTicks.Value);
                    _logger.Trace("Dequeue time {0}", dequeuedTime);

                    if (DateTime.UtcNow.Subtract(dequeuedTime) <= _workItemTimeout)
                        continue;

                    _logger.Trace("Auto abandon item {0}", workId);

                    var entry = await GetQueueEntry(workId).AnyContext();
                    await AbandonAsync(entry).AnyContext();
                    Interlocked.Increment(ref _workItemTimeoutCount);
                }
            } catch (Exception ex) {
                _logger.Error(ex, "Error checking for work item timeouts: {0}", ex.Message);
            }

            try {
                var waitIds = await Database.ListRangeAsync(WaitListName).AnyContext();
                foreach (var waitId in waitIds) {
                    var waitTimeTicks = await _cache.GetAsync<long>(GetWaitTimeKey(waitId)).AnyContext();

                    _logger.Trace("Wait time: {0}", waitTimeTicks);

                    if (waitTimeTicks.HasValue && waitTimeTicks.Value > DateTime.UtcNow.Ticks)
                        continue;

                    _logger.Trace("Getting retry lock");
                    _logger.Trace("Adding item back to queue for retry: {0}", waitId);

                    var tx = Database.CreateTransaction();
                    tx.ListRemoveAsync(WaitListName, waitId);
                    tx.ListLeftPushAsync(QueueListName, waitId);
                    var success = await tx.ExecuteAsync().AnyContext();
                    if (!success)
                        throw new Exception("Unable to move item to queue list.");

                    await Database.KeyDeleteAsync(GetWaitTimeKey(waitId)).AnyContext();
                    await _subscriber.PublishAsync(GetTopicName(), waitId).AnyContext();
                }
            } catch (Exception ex) {
                _logger.Error(ex, "Error adding items back to the queue after the retry delay: {0}", ex.Message);
            }

            try {
                await TrimDeadletterItemsAsync(_deadLetterMaxItems).AnyContext();
            } catch (Exception ex) {
                _logger.Error(ex, "Error trimming deadletter items: {0}", ex.Message);
            }
        }

        private async Task DoMaintenanceWorkLoop(CancellationToken cancellationToken) {
            while (!cancellationToken.IsCancellationRequested) {
                _logger.Trace("Requesting Maintenance Lock: Name={0} Id={1}", _queueName, QueueId);

                await _maintenanceLockProvider.TryUsingAsync(_queueName + "-maintenance", async () => await DoMaintenanceWorkAsync().AnyContext(), acquireTimeout: TimeSpan.FromSeconds(30));
            }
        }

        public override async void Dispose() {
            _logger.Trace("Queue {0} dispose", _queueName);

            await _subscriber.UnsubscribeAllAsync().AnyContext();
            _queueDisposedCancellationTokenSource?.Cancel();

            base.Dispose();
        }
    }
}
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
using Foundatio.Caching;
using Foundatio.Extensions;
using Foundatio.Lock;
using Foundatio.Redis;
using Foundatio.Redis.Utility;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
#pragma warning disable 4014

namespace Foundatio.Queues;

public class RedisQueue<T> : QueueBase<T, RedisQueueOptions<T>> where T : class
{
    private readonly AsyncLock _lock = new();
    private readonly AsyncAutoResetEvent _autoResetEvent = new();
    private readonly ISubscriber _subscriber;
    private readonly RedisCacheClient _cache;
    private long _enqueuedCount;
    private long _dequeuedCount;
    private long _completedCount;
    private long _abandonedCount;
    private long _workerErrorCount;
    private long _workItemTimeoutCount;
    private readonly ILockProvider _maintenanceLockProvider;
    private Task _maintenanceTask;
    private bool _isSubscribed;
    private readonly TimeSpan _payloadTimeToLive;
    private bool _scriptsLoaded;
    private readonly string _listPrefix;

    private LoadedLuaScript _dequeueId;

    public RedisQueue(RedisQueueOptions<T> options) : base(options)
    {
        if (options.ConnectionMultiplexer == null)
            throw new ArgumentException("ConnectionMultiplexer is required.");

        options.ConnectionMultiplexer.ConnectionRestored += ConnectionMultiplexerOnConnectionRestored;
        _cache = new RedisCacheClient(new RedisCacheClientOptions { ConnectionMultiplexer = options.ConnectionMultiplexer, Serializer = _serializer });

        _payloadTimeToLive = GetPayloadTtl();
        _subscriber = _options.ConnectionMultiplexer.GetSubscriber();

        _listPrefix = _options.ConnectionMultiplexer.IsCluster() ? "{q:" + _options.Name + "}" : $"q:{_options.Name}";
        _queueListName = $"{_listPrefix}:in";
        _workListName = $"{_listPrefix}:work";
        _waitListName = $"{_listPrefix}:wait";
        _deadListName = $"{_listPrefix}:dead";

        // min is 1 second, max is 1 minute
        var interval = _options.WorkItemTimeout > TimeSpan.FromSeconds(1) ? _options.WorkItemTimeout.Min(TimeSpan.FromMinutes(1)) : TimeSpan.FromSeconds(1);
        _maintenanceLockProvider = new ThrottlingLockProvider(_cache, 1, interval);

        _logger.LogInformation("Queue {QueueId} created. Retries: {Retries} Retry Delay: {RetryDelay:g}, Maintenance Interval: {MaintenanceInterval:g}", QueueId, _options.Retries, _options.RetryDelay, interval);
    }

    public RedisQueue(Builder<RedisQueueOptionsBuilder<T>, RedisQueueOptions<T>> config)
        : this(config(new RedisQueueOptionsBuilder<T>()).Build()) { }

    public IDatabase Database => _options.ConnectionMultiplexer.GetDatabase();

    protected override Task EnsureQueueCreatedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    private bool IsMaintenanceRunning => !_options.RunMaintenanceTasks || _maintenanceTask != null && !_maintenanceTask.IsCanceled && !_maintenanceTask.IsFaulted && !_maintenanceTask.IsCompleted;
    private async Task EnsureMaintenanceRunningAsync()
    {
        if (_queueDisposedCancellationTokenSource.IsCancellationRequested || IsMaintenanceRunning)
            return;

        using (await _lock.LockAsync(_queueDisposedCancellationTokenSource.Token).AnyContext())
        {
            if (_queueDisposedCancellationTokenSource.IsCancellationRequested || _maintenanceTask != null)
                return;

            _logger.LogTrace("Starting maintenance for {Name}", _options.Name);
            _maintenanceTask = Task.Run(() => DoMaintenanceWorkLoopAsync());
        }
    }

    private async Task EnsureTopicSubscriptionAsync()
    {
        if (_queueDisposedCancellationTokenSource.IsCancellationRequested || _isSubscribed)
            return;

        using (await _lock.LockAsync(_queueDisposedCancellationTokenSource.Token).AnyContext())
        {
            if (_queueDisposedCancellationTokenSource.IsCancellationRequested || _isSubscribed)
                return;

            _logger.LogTrace("Subscribing to enqueue messages for {Name}", _options.Name);
            await _subscriber.SubscribeAsync(RedisChannel.Literal(GetTopicName()), OnTopicMessage).AnyContext();
            _isSubscribed = true;
            _logger.LogTrace("Subscribed to enqueue messages for {Name}", _options.Name);
        }
    }

    protected override Task<QueueStats> GetQueueStatsImplAsync()
    {
        var queued = Database.ListLengthAsync(_queueListName);
        var wait = Database.ListLengthAsync(_waitListName);
        var working = Database.ListLengthAsync(_workListName);
        var deadLetter = Database.ListLengthAsync(_deadListName);

        return Task.WhenAll(queued, wait, working, deadLetter)
            .ContinueWith(_ => new QueueStats
            {
                Queued = queued.Result + wait.Result,
                Working = working.Result,
                Deadletter = deadLetter.Result,
                Enqueued = _enqueuedCount,
                Dequeued = _dequeuedCount,
                Completed = _completedCount,
                Abandoned = _abandonedCount,
                Errors = _workerErrorCount,
                Timeouts = _workItemTimeoutCount
            }, TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    protected override QueueStats GetMetricsQueueStats()
    {
        long queued = Database.ListLength(_queueListName);
        long wait = Database.ListLength(_waitListName);
        long working = Database.ListLength(_workListName);
        long deadLetter = Database.ListLength(_deadListName);

        return new QueueStats
        {
            Queued = queued + wait,
            Working = working,
            Deadletter = deadLetter,
            Enqueued = _enqueuedCount,
            Dequeued = _dequeuedCount,
            Completed = _completedCount,
            Abandoned = _abandonedCount,
            Errors = _workerErrorCount,
            Timeouts = _workItemTimeoutCount
        };
    }

    private readonly string _queueListName;
    private readonly string _workListName;
    private readonly string _waitListName;
    private readonly string _deadListName;

    private string GetPayloadKey(string id)
    {
        return String.Concat(_listPrefix, ":", id);
    }

    private TimeSpan GetPayloadTtl()
    {
        var ttl = TimeSpan.Zero;
        for (int attempt = 1; attempt <= _options.Retries + 1; attempt++)
            ttl = ttl.Add(GetRetryDelay(attempt));

        // minimum of 7 days for payload
        return TimeSpan.FromMilliseconds(Math.Max(ttl.TotalMilliseconds * 1.5, TimeSpan.FromDays(7).TotalMilliseconds));
    }

    private string GetAttemptsKey(string id)
    {
        return String.Concat(_listPrefix, ":", id, ":attempts");
    }

    private TimeSpan GetAttemptsTtl()
    {
        return _payloadTimeToLive;
    }

    private string GetEnqueuedTimeKey(string id)
    {
        return String.Concat(_listPrefix, ":", id, ":enqueued");
    }

    private string GetDequeuedTimeKey(string id)
    {
        return String.Concat(_listPrefix, ":", id, ":dequeued");
    }

    private string GetRenewedTimeKey(string id)
    {
        return String.Concat(_listPrefix, ":", id, ":renewed");
    }

    private TimeSpan GetWorkItemTimeoutTimeTtl()
    {
        return TimeSpan.FromMilliseconds(Math.Max(_options.WorkItemTimeout.TotalMilliseconds * 1.5, TimeSpan.FromHours(1).TotalMilliseconds));
    }

    private string GetWaitTimeKey(string id)
    {
        return String.Concat(_listPrefix, ":", id, ":wait");
    }

    private TimeSpan GetWaitTimeTtl()
    {
        return _payloadTimeToLive;
    }

    private string GetTopicName()
    {
        return String.Concat(_listPrefix, ":in");
    }

    protected override async Task<string> EnqueueImplAsync(T data, QueueEntryOptions options)
    {
        string id = Guid.NewGuid().ToString("N");
        if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("Queue {Name} enqueue item: {EntryId}", _options.Name, id);

        if (options.DeliveryDelay.HasValue && options.DeliveryDelay.Value > TimeSpan.Zero)
            throw new NotSupportedException("DeliveryDelay is not supported in the Redis queue implementation.");

        bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
        if (!await OnEnqueuingAsync(data, options).AnyContext())
        {
            if (isTraceLogLevelEnabled) _logger.LogTrace("Aborting enqueue item: {EntryId}", id);
            return null;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var envelope = new RedisPayloadEnvelope<T>
        {
            Properties = options.Properties,
            CorrelationId = options.CorrelationId,
            Value = data
        };
        bool success = await Run.WithRetriesAsync(() => _cache.AddAsync(GetPayloadKey(id), envelope, _payloadTimeToLive), logger: _logger).AnyContext();
        if (!success)
            throw new InvalidOperationException("Attempt to set payload failed.");

        await Run.WithRetriesAsync(() => Task.WhenAll(
            _cache.SetAsync(GetEnqueuedTimeKey(id), now.Ticks, _payloadTimeToLive),
            Database.ListLeftPushAsync(_queueListName, id)
        ), logger: _logger).AnyContext();

        try
        {
            _autoResetEvent.Set();
            await Run.WithRetriesAsync(() => _subscriber.PublishAsync(RedisChannel.Literal(GetTopicName()), id), logger: _logger).AnyContext();
        }
        catch (Exception ex)
        {
            if (isTraceLogLevelEnabled) _logger.LogTrace(ex, "Error publishing topic message");
        }

        Interlocked.Increment(ref _enqueuedCount);
        var entry = new QueueEntry<T>(id, options.CorrelationId, data, this, now, 0);
        await OnEnqueuedAsync(entry).AnyContext();

        if (isTraceLogLevelEnabled) _logger.LogTrace("Enqueue done");
        return id;
    }

    private readonly List<Task> _workers = [];

    protected override void StartWorkingImpl(Func<IQueueEntry<T>, CancellationToken, Task> handler, bool autoComplete, CancellationToken cancellationToken)
    {
        if (handler == null)
            throw new ArgumentNullException(nameof(handler));

        _logger.LogTrace("Queue {Name} start working", _options.Name);

        _workers.Add(Task.Run(async () =>
        {
            using var linkedCancellationToken = GetLinkedDisposableCancellationTokenSource(cancellationToken);
            _logger.LogTrace("WorkerLoop Start {Name}", _options.Name);

            while (!linkedCancellationToken.IsCancellationRequested)
            {
                _logger.LogTrace("WorkerLoop Signaled {Name}", _options.Name);

                IQueueEntry<T> queueEntry = null;
                try
                {
                    queueEntry = await DequeueImplAsync(linkedCancellationToken.Token).AnyContext();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error on Dequeue: {Message}", ex.Message);
                }

                if (linkedCancellationToken.IsCancellationRequested || queueEntry == null)
                    continue;

                try
                {
                    await handler(queueEntry, linkedCancellationToken.Token).AnyContext();
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _workerErrorCount);
                    _logger.LogError(ex, "Worker error: {Message}", ex.Message);

                    if (!queueEntry.IsAbandoned && !queueEntry.IsCompleted)
                    {
                        try
                        {
                            await queueEntry.AbandonAsync().AnyContext();
                        }
                        catch (Exception abandonEx)
                        {
                            _logger.LogError(abandonEx, "Worker error abandoning queue entry: {Message}", abandonEx.Message);
                        }
                    }
                }

                if (autoComplete && !queueEntry.IsAbandoned && !queueEntry.IsCompleted)
                {
                    try
                    {
                        await Run.WithRetriesAsync(() => queueEntry.CompleteAsync(), cancellationToken: linkedCancellationToken.Token, logger: _logger).AnyContext();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Worker error attempting to auto complete entry: {Message}", ex.Message);
                    }
                }
            }

            _logger.LogTrace("Worker exiting: {Name} Cancel Requested: {IsCancellationRequested}", _options.Name, linkedCancellationToken.IsCancellationRequested);
        }, GetLinkedDisposableCancellationTokenSource(cancellationToken).Token));
    }

    protected override async Task<IQueueEntry<T>> DequeueImplAsync(CancellationToken linkedCancellationToken)
    {
        _logger.LogTrace("Queue {Name} dequeuing item...", _options.Name);

        if (!IsMaintenanceRunning)
            await EnsureMaintenanceRunningAsync().AnyContext();
        if (!_isSubscribed)
            await EnsureTopicSubscriptionAsync().AnyContext();

        var value = await DequeueIdAsync(linkedCancellationToken).AnyContext();
        if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Initial list value: {Value}", value.IsNullOrEmpty ? "<null>" : value.ToString());

        while (value.IsNullOrEmpty && !linkedCancellationToken.IsCancellationRequested)
        {
            _logger.LogTrace("Waiting to dequeue item...");
            var sw = Stopwatch.StartNew();

            try
            {
                using var timeoutCancellationTokenSource = new CancellationTokenSource(10000);
                using var dequeueCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(linkedCancellationToken, timeoutCancellationTokenSource.Token);
                await _autoResetEvent.WaitAsync(dequeueCancellationTokenSource.Token).AnyContext();
            }
            catch (OperationCanceledException) { }

            sw.Stop();
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Waited for dequeue: {Elapsed}", sw.Elapsed.ToString());

            value = await DequeueIdAsync(linkedCancellationToken).AnyContext();
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("List value: {Value}", value.IsNullOrEmpty ? "<null>" : value.ToString());
        }

        if (value.IsNullOrEmpty)
            return null;

        try
        {
            var entry = await GetQueueEntryAsync(value).AnyContext();
            if (entry == null)
                return null;

            Interlocked.Increment(ref _dequeuedCount);
            await OnDequeuedAsync(entry).AnyContext();

            _logger.LogDebug("Dequeued item: {Value}", value);
            return entry;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting dequeued item payload: {Value}", value);
            throw;
        }
    }

    public override async Task RenewLockAsync(IQueueEntry<T> entry)
    {
        if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("Queue {Name} renew lock item: {EntryId}", _options.Name, entry.Id);
        await Run.WithRetriesAsync(() => _cache.SetAsync(GetRenewedTimeKey(entry.Id), _timeProvider.GetUtcNow().Ticks, GetWorkItemTimeoutTimeTtl()), logger: _logger).AnyContext();
        await OnLockRenewedAsync(entry).AnyContext();
        if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Renew lock done: {EntryId}", entry.Id);
    }

    private async Task<QueueEntry<T>> GetQueueEntryAsync(string workId)
    {
        var payload = await Run.WithRetriesAsync(() => _cache.GetAsync<RedisPayloadEnvelope<T>>(GetPayloadKey(workId)), logger: _logger).AnyContext();
        if (payload.IsNull)
        {
            if (_logger.IsEnabled(LogLevel.Error)) _logger.LogError("Error getting queue payload: {WorkId}", workId);
            await Database.ListRemoveAsync(_workListName, workId).AnyContext();
            return null;
        }

        var enqueuedTimeTicks = Run.WithRetriesAsync(() => _cache.GetAsync<long>(GetEnqueuedTimeKey(workId), 0), logger: _logger);
        var attemptsValue = Run.WithRetriesAsync(() => _cache.GetAsync(GetAttemptsKey(workId), 0), logger: _logger);
        await Task.WhenAll(enqueuedTimeTicks, attemptsValue).AnyContext();

        var queueEntry = new QueueEntry<T>(workId, payload.Value.CorrelationId, payload.Value.Value, this, new DateTime(enqueuedTimeTicks.Result, DateTimeKind.Utc), attemptsValue.Result + 1);

        if (payload.Value.Properties != null)
        {
            foreach (var property in payload.Value.Properties)
                queueEntry.Properties.Add(property.Key, property.Value);
        }

        return queueEntry;
    }

    private async Task<RedisValue> DequeueIdAsync(CancellationToken linkedCancellationToken)
    {
        try
        {
            return await Run.WithRetriesAsync(async () =>
            {
                var timeout = GetWorkItemTimeoutTimeTtl();
                long now = _timeProvider.GetUtcNow().Ticks;

                await LoadScriptsAsync().AnyContext();
                var result = await Database.ScriptEvaluateAsync(_dequeueId, new
                {
                    queueListName = (RedisKey)_queueListName,
                    workListName = (RedisKey)_workListName,
                    listPrefix = _listPrefix,
                    now,
                    timeout = timeout.TotalMilliseconds
                }).AnyContext();
                return result.ToString();
            }, 3, TimeSpan.FromMilliseconds(100), _timeProvider, linkedCancellationToken, _logger).AnyContext();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Queue {Name} dequeue id async error: {Error}", _options.Name, ex.Message);
            return RedisValue.Null;
        }
    }

    public override async Task CompleteAsync(IQueueEntry<T> entry)
    {
        if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("Queue {Name} complete item: {EntryId}", _options.Name, entry.Id);
        if (entry.IsAbandoned || entry.IsCompleted)
        {
            //_logger.LogDebug("Queue {Name} item already abandoned or completed: {EntryId}", _options.Name, entry.Id);
            throw new InvalidOperationException("Queue entry has already been completed or abandoned.");
        }

        long result = await Run.WithRetriesAsync(() => Database.ListRemoveAsync(_workListName, entry.Id), logger: _logger).AnyContext();
        if (result == 0)
        {
            _logger.LogDebug("Queue {Name} item not in work list: {EntryId}", _options.Name, entry.Id);
            throw new InvalidOperationException("Queue entry not in work list, it may have been auto abandoned.");
        }

        await Run.WithRetriesAsync(() => Task.WhenAll(
            Database.KeyDeleteAsync(GetPayloadKey(entry.Id)),
            Database.KeyDeleteAsync(GetAttemptsKey(entry.Id)),
            Database.KeyDeleteAsync(GetEnqueuedTimeKey(entry.Id)),
            Database.KeyDeleteAsync(GetDequeuedTimeKey(entry.Id)),
            Database.KeyDeleteAsync(GetRenewedTimeKey(entry.Id)),
            Database.KeyDeleteAsync(GetWaitTimeKey(entry.Id))
        ), logger: _logger).AnyContext();

        Interlocked.Increment(ref _completedCount);
        entry.MarkCompleted();
        await OnCompletedAsync(entry).AnyContext();
        if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Complete done: {EntryId}", entry.Id);
    }

    public override async Task AbandonAsync(IQueueEntry<T> entry)
    {
        _logger.LogDebug("Queue {Name}:{QueueId} abandon item: {EntryId}", _options.Name, QueueId, entry.Id);
        if (entry.IsAbandoned || entry.IsCompleted)
        {
            _logger.LogError("Queue {Name}:{QueueId} unable to abandon item because already abandoned or completed: {EntryId}", _options.Name, QueueId, entry.Id);
            throw new InvalidOperationException("Queue entry has already been completed or abandoned.");
        }

        string attemptsCacheKey = GetAttemptsKey(entry.Id);
        var attemptsCachedValue = await Run.WithRetriesAsync(() => _cache.GetAsync<int>(attemptsCacheKey), logger: _logger).AnyContext();
        int attempts = 1;
        if (attemptsCachedValue.HasValue)
            attempts = attemptsCachedValue.Value + 1;

        var retryDelay = GetRetryDelay(attempts);
        _logger.LogInformation("Item: {EntryId}, Retry attempts: {RetryAttempts}, Retries Allowed: {Retries}, Retry Delay: {RetryDelay:g}", entry.Id, attempts - 1, _options.Retries, retryDelay);

        if (attempts > _options.Retries)
        {
            _logger.LogInformation("Exceeded retry limit moving to deadletter: {EntryId}", entry.Id);

            var tx = Database.CreateTransaction();
            tx.AddCondition(Condition.KeyExists(GetRenewedTimeKey(entry.Id)));
            tx.ListRemoveAsync(_workListName, entry.Id);
            tx.ListLeftPushAsync(_deadListName, entry.Id);
            tx.KeyDeleteAsync(GetRenewedTimeKey(entry.Id));
            tx.KeyExpireAsync(GetPayloadKey(entry.Id), _options.DeadLetterTimeToLive);
            bool success = await Run.WithRetriesAsync(() => tx.ExecuteAsync(), logger: _logger).AnyContext();
            if (!success)
                throw new InvalidOperationException("Queue entry not in work list, it may have been auto abandoned.");

            await Run.WithRetriesAsync(() => Task.WhenAll(
                _cache.IncrementAsync(attemptsCacheKey, 1, GetAttemptsTtl()),
                Database.KeyDeleteAsync(GetDequeuedTimeKey(entry.Id)),
                Database.KeyDeleteAsync(GetWaitTimeKey(entry.Id))
            ), logger: _logger).AnyContext();
        }
        else if (retryDelay > TimeSpan.Zero)
        {
            _logger.LogInformation("Adding item to wait list for future retry: {EntryId}", entry.Id);

            await Run.WithRetriesAsync(() => Task.WhenAll(
                _cache.SetAsync(GetWaitTimeKey(entry.Id), _timeProvider.GetUtcNow().Add(retryDelay).Ticks, GetWaitTimeTtl()),
                _cache.IncrementAsync(attemptsCacheKey, 1, GetAttemptsTtl())
            ), logger: _logger).AnyContext();

            var tx = Database.CreateTransaction();
            tx.AddCondition(Condition.KeyExists(GetRenewedTimeKey(entry.Id)));
            tx.ListRemoveAsync(_workListName, entry.Id);
            tx.ListLeftPushAsync(_waitListName, entry.Id);
            tx.KeyDeleteAsync(GetRenewedTimeKey(entry.Id));
            bool success = await Run.WithRetriesAsync(() => tx.ExecuteAsync()).AnyContext();
            if (!success)
                throw new InvalidOperationException("Queue entry not in work list, it may have been auto abandoned.");

            await Run.WithRetriesAsync(() => Database.KeyDeleteAsync(GetDequeuedTimeKey(entry.Id)), logger: _logger).AnyContext();
        }
        else
        {
            _logger.LogInformation("Adding item back to queue for retry: {EntryId}", entry.Id);

            await Run.WithRetriesAsync(() => _cache.IncrementAsync(attemptsCacheKey, 1, GetAttemptsTtl()), logger: _logger).AnyContext();

            var tx = Database.CreateTransaction();
            tx.AddCondition(Condition.KeyExists(GetRenewedTimeKey(entry.Id)));
            tx.ListRemoveAsync(_workListName, entry.Id);
            tx.ListLeftPushAsync(_queueListName, entry.Id);
            tx.KeyDeleteAsync(GetRenewedTimeKey(entry.Id));
            bool success = await Run.WithRetriesAsync(() => tx.ExecuteAsync(), logger: _logger).AnyContext();
            if (!success)
                throw new InvalidOperationException("Queue entry not in work list, it may have been auto abandoned.");

            await Run.WithRetriesAsync(() => Task.WhenAll(
                Database.KeyDeleteAsync(GetDequeuedTimeKey(entry.Id)),
                // This should pulse the monitor.
                _subscriber.PublishAsync(RedisChannel.Literal(GetTopicName()), entry.Id)
            ), logger: _logger).AnyContext();
        }

        Interlocked.Increment(ref _abandonedCount);
        entry.MarkAbandoned();
        await OnAbandonedAsync(entry).AnyContext();
        _logger.LogInformation("Abandon complete: {EntryId}", entry.Id);
    }

    private TimeSpan GetRetryDelay(int attempts)
    {
        if (_options.RetryDelay <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        int maxMultiplier = _options.RetryMultipliers.Length > 0 ? _options.RetryMultipliers.Last() : 1;
        int multiplier = attempts <= _options.RetryMultipliers.Length ? _options.RetryMultipliers[attempts - 1] : maxMultiplier;
        return TimeSpan.FromMilliseconds(_options.RetryDelay.TotalMilliseconds * multiplier);
    }

    protected override Task<IEnumerable<T>> GetDeadletterItemsImplAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public override async Task DeleteQueueAsync()
    {
        if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Deleting queue: {Name}", _options.Name);
        await Task.WhenAll(
            DeleteListAsync(_queueListName),
            DeleteListAsync(_workListName),
            DeleteListAsync(_waitListName),
            DeleteListAsync(_deadListName)
        ).AnyContext();

        _enqueuedCount = 0;
        _dequeuedCount = 0;
        _completedCount = 0;
        _abandonedCount = 0;
        _workerErrorCount = 0;
    }

    private async Task DeleteListAsync(string name)
    {
        var itemIds = await Database.ListRangeAsync(name).AnyContext();
        var tasks = new List<Task>();
        foreach (var id in itemIds)
        {
            tasks.AddRange(new Task[] {
                Database.KeyDeleteAsync(GetPayloadKey(id)),
                Database.KeyDeleteAsync(GetAttemptsKey(id)),
                Database.KeyDeleteAsync(GetEnqueuedTimeKey(id)),
                Database.KeyDeleteAsync(GetDequeuedTimeKey(id)),
                Database.KeyDeleteAsync(GetRenewedTimeKey(id)),
                Database.KeyDeleteAsync(GetWaitTimeKey(id))
            });
        }

        tasks.Add(Database.KeyDeleteAsync(name));
        await Task.WhenAll(tasks).AnyContext();
    }

    private async Task TrimDeadletterItemsAsync(int maxItems)
    {
        var itemIds = (await Database.ListRangeAsync(_deadListName).AnyContext()).Skip(maxItems);
        var tasks = new List<Task>();
        foreach (var id in itemIds)
        {
            tasks.AddRange(new Task[] {
                Database.KeyDeleteAsync(GetPayloadKey(id)),
                Database.KeyDeleteAsync(GetAttemptsKey(id)),
                Database.KeyDeleteAsync(GetEnqueuedTimeKey(id)),
                Database.KeyDeleteAsync(GetDequeuedTimeKey(id)),
                Database.KeyDeleteAsync(GetRenewedTimeKey(id)),
                Database.KeyDeleteAsync(GetWaitTimeKey(id)),
                Database.ListRemoveAsync(_queueListName, id),
                Database.ListRemoveAsync(_workListName, id),
                Database.ListRemoveAsync(_waitListName, id),
                Database.ListRemoveAsync(_deadListName, id)
            });
        }

        await Task.WhenAll(tasks).AnyContext();
    }

    private void OnTopicMessage(RedisChannel redisChannel, RedisValue redisValue)
    {
        if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Queue OnMessage {Name}: {Value}", _options.Name, redisValue);
        _autoResetEvent.Set();
    }

    private void ConnectionMultiplexerOnConnectionRestored(object sender, ConnectionFailedEventArgs connectionFailedEventArgs)
    {
        if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("Redis connection restored");
        _scriptsLoaded = false;
        _autoResetEvent.Set();
    }

    public async Task DoMaintenanceWorkAsync()
    {
        if (_queueDisposedCancellationTokenSource.IsCancellationRequested)
            return;

        _logger.LogTrace("Starting DoMaintenance: Name: {Name} Id: {Id}", _options.Name, QueueId);
        var utcNow = _timeProvider.GetUtcNow();

        try
        {
            var workIds = await Database.ListRangeAsync(_workListName).AnyContext();
            foreach (var workId in workIds)
            {
                if (_queueDisposedCancellationTokenSource.IsCancellationRequested)
                    return;

                var renewedTimeTicks = await _cache.GetAsync<long>(GetRenewedTimeKey(workId)).AnyContext();
                if (!renewedTimeTicks.HasValue)
                {
                    _logger.LogTrace("Skipping {WorkId}: no renewed time", workId);
                    continue;
                }

                var renewedTime = new DateTimeOffset(new DateTime(renewedTimeTicks.Value), TimeSpan.Zero);
                _logger.LogTrace("{WorkId}: Renewed time {RenewedTime:o}", workId, renewedTime);

                if (utcNow.Subtract(renewedTime) <= _options.WorkItemTimeout)
                    continue;

                _logger.LogInformation("{WorkId} Auto abandon item. Renewed: {RenewedTime:o} Current: {UtcNow:o} Timeout: {WorkItemTimeout:g} QueueId: {QueueId}", workId, renewedTime, utcNow, _options.WorkItemTimeout, QueueId);
                var entry = await GetQueueEntryAsync(workId).AnyContext();
                if (entry == null)
                {
                    _logger.LogError("{WorkId} Error getting queue entry for work item timeout", workId);
                    continue;
                }

                _logger.LogError("{WorkId} AbandonAsync", workId);
                await AbandonAsync(entry).AnyContext();
                Interlocked.Increment(ref _workItemTimeoutCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking for work item timeouts: {Message}", ex.Message);
        }

        if (_queueDisposedCancellationTokenSource.IsCancellationRequested)
            return;

        try
        {
            var waitIds = await Database.ListRangeAsync(_waitListName).AnyContext();
            foreach (var waitId in waitIds)
            {
                if (_queueDisposedCancellationTokenSource.IsCancellationRequested)
                    return;

                var waitTimeTicks = await _cache.GetAsync<long>(GetWaitTimeKey(waitId)).AnyContext();
                _logger.LogTrace("{WaitId}: Wait time {WaitTime}", waitId, waitTimeTicks);

                if (waitTimeTicks.HasValue && waitTimeTicks.Value > utcNow.Ticks)
                    continue;

                _logger.LogTrace("{WaitId}: Getting retry lock", waitId);
                _logger.LogDebug("{WaitId}: Adding item back to queue for retry", waitId);

                var tx = Database.CreateTransaction();
                tx.ListRemoveAsync(_waitListName, waitId);
                tx.ListLeftPushAsync(_queueListName, waitId);
                tx.KeyDeleteAsync(GetWaitTimeKey(waitId));
                bool success = await Run.WithRetriesAsync(() => tx.ExecuteAsync(), logger: _logger).AnyContext();
                if (!success)
                    throw new Exception("Unable to move item to queue list.");

                await Run.WithRetriesAsync(() => _subscriber.PublishAsync(RedisChannel.Literal(GetTopicName()), waitId), cancellationToken: _queueDisposedCancellationTokenSource.Token, logger: _logger).AnyContext();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding items back to the queue after the retry delay: {Message}", ex.Message);
        }

        if (_queueDisposedCancellationTokenSource.IsCancellationRequested)
            return;

        try
        {
            await TrimDeadletterItemsAsync(_options.DeadLetterMaxItems).AnyContext();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error trimming deadletter items: {0}", ex.Message);
        }

        _logger.LogTrace("Finished DoMaintenance: Name: {Name} Id: {Id} Duration: {Duration:g}", _options.Name, QueueId, _timeProvider.GetUtcNow().Subtract(utcNow));
    }

    private async Task DoMaintenanceWorkLoopAsync()
    {
        while (!_queueDisposedCancellationTokenSource.IsCancellationRequested)
        {
            _logger.LogTrace("Requesting Maintenance Lock. Name: {Name} Id: {Id}", _options.Name, QueueId);

            var utcNow = _timeProvider.GetUtcNow();
            using var linkedCancellationToken = GetLinkedDisposableCancellationTokenSource(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
            bool gotLock = await _maintenanceLockProvider.TryUsingAsync($"{_options.Name}-maintenance", DoMaintenanceWorkAsync, cancellationToken: linkedCancellationToken.Token).AnyContext();

            _logger.LogTrace("{Status} Maintenance Lock. Name: {Name} Id: {Id} Time To Acquire: {AcquireDuration:g}", gotLock ? "Acquired" : "Failed to acquire", _options.Name, QueueId, _timeProvider.GetUtcNow().Subtract(utcNow));
        }
    }

    private async Task LoadScriptsAsync()
    {
        if (_scriptsLoaded)
            return;

        using (await _lock.LockAsync().AnyContext())
        {
            if (_scriptsLoaded)
                return;

            var dequeueId = LuaScript.Prepare(DequeueIdScript);

            foreach (var endpoint in _options.ConnectionMultiplexer.GetEndPoints())
            {
                var server = _options.ConnectionMultiplexer.GetServer(endpoint);
                if (server.IsReplica)
                    continue;

                _dequeueId = await dequeueId.LoadAsync(server).AnyContext();
            }

            _scriptsLoaded = true;
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        _options.ConnectionMultiplexer.ConnectionRestored -= ConnectionMultiplexerOnConnectionRestored;

        if (_isSubscribed)
        {
            lock (_lock.Lock())
            {
                if (_isSubscribed)
                {
                    _logger.LogTrace("Unsubscribing from topic {Topic}", GetTopicName());
                    _subscriber.Unsubscribe(RedisChannel.Literal(GetTopicName()), OnTopicMessage, CommandFlags.FireAndForget);
                    _isSubscribed = false;
                    _logger.LogTrace("Unsubscribed from topic {Topic}", GetTopicName());
                }
            }
        }

        _logger.LogTrace("Got {WorkerCount} workers to cleanup", _workers.Count);
        foreach (var worker in _workers)
        {
            if (worker.IsCompleted)
                continue;

            _logger.LogTrace("Attempting to cleanup worker");
            if (!worker.Wait(TimeSpan.FromSeconds(5)))
                _logger.LogError("Failed waiting for worker to stop");
        }

        _cache.Dispose();
    }

    private static readonly string DequeueIdScript = EmbeddedResourceLoader.GetEmbeddedResource("Foundatio.Redis.Scripts.DequeueId.lua");
}

public class RedisPayloadEnvelope<T>
{
    public string CorrelationId { get; set; }
    public IDictionary<string, string> Properties { get; set; }
    public T Value { get; set; }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using StackExchange.Redis;

namespace Foundatio.Jobs;

/// <summary>
/// A Redis-backed durable job and scheduled-dispatch store. Admission, claims, and ownership-guarded mutations
/// are atomic. Sorted indexes bound monitoring pages, due claims, and terminal retention cleanup.
/// </summary>
public sealed partial class RedisJobRuntimeStore : IJobRuntimeStore
{
    private const string ClaimDueScript = """
        local ids = redis.call('ZRANGEBYSCORE', KEYS[1], '-inf', ARGV[1], 'LIMIT', 0, tonumber(ARGV[2]))
        local claimed = {}
        for _, id in ipairs(ids) do
            local dkey = ARGV[5] .. id
            if redis.call('EXISTS', dkey) == 1 then
                local owner = redis.call('HGET', dkey, 'claimOwner')
                local expires = redis.call('HGET', dkey, 'claimExpiresUtc')
                if (not owner or owner == '') or (expires and expires ~= '' and tonumber(expires) <= tonumber(ARGV[1])) then
                    redis.call('HSET', dkey, 'claimOwner', ARGV[3], 'claimExpiresUtc', ARGV[4])
                    redis.call('HINCRBY', dkey, 'attempts', 1)
                    redis.call('ZADD', KEYS[1], ARGV[4], id)
                    table.insert(claimed, redis.call('HGETALL', dkey))
                end
            else
                redis.call('ZREM', KEYS[1], id)
            end
        end
        return claimed
        """;

    private readonly IDatabase _db;
    private readonly string _prefix;
    private readonly TimeProvider _timeProvider;
    private readonly JobRuntimeStoreOptions _options;

    public RedisJobRuntimeStore(RedisJobRuntimeStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.ConnectionMultiplexer);
        options.Runtime.Validate();
        _options = options.Runtime;
        _db = options.ConnectionMultiplexer.GetDatabase();
        _prefix = options.KeyPrefix ?? "";
        _timeProvider = options.TimeProvider ?? TimeProvider.System;
    }

    public RedisJobRuntimeStore(IConnectionMultiplexer connectionMultiplexer, string keyPrefix = "fnd:jobs:", TimeProvider? timeProvider = null)
        : this(new RedisJobRuntimeStoreOptions { ConnectionMultiplexer = connectionMultiplexer, KeyPrefix = keyPrefix, TimeProvider = timeProvider }) { }

    public async Task CreateIfAbsentAsync(JobState initial, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initial);
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePayload(initial.Payload?.Length ?? 0);
        initial.Validate();
        initial.RetryPolicy.Validate();
        var now = _timeProvider.GetUtcNow();
        var state = initial with { CreatedUtc = initial.CreatedUtc == default ? now : initial.CreatedUtc, LastUpdatedUtc = initial.LastUpdatedUtc == default ? now : initial.LastUpdatedUtc };
        const string script = MonitoringFunctions + "\n" + """
            if purgeBrokerHistory(ARGV[8], tonumber(ARGV[6]), 128) == 128 then return -3 end
            if redis.call('EXISTS', KEYS[1]) == 1 then return 0 end
            redis.call('ZREMRANGEBYSCORE', KEYS[6], '-inf', ARGV[6])
            if redis.call('ZSCORE', KEYS[6], ARGV[1]) then return 0 end
            if redis.call('ZCARD', KEYS[2]) - redis.call('ZCARD', KEYS[5]) >= tonumber(ARGV[2]) then return -1 end
            if redis.call('ZCARD', KEYS[6]) >= tonumber(ARGV[5]) then return -2 end
            redis.call('HSET', KEYS[1], unpack(ARGV, 9))
            redis.call('ZADD', KEYS[6], ARGV[7], ARGV[1])
            local expiry = redis.call('HGET', KEYS[1], 'expiresUtc')
            if expiry then redis.call('ZADD', KEYS[7], expiry, ARGV[1]) end
            redis.call('ZADD', KEYS[2], 0, ARGV[1])
            redis.call('ZADD', KEYS[3], 0, ARGV[1])
            redis.call('ZADD', KEYS[4], 0, ARGV[1])
            local ready = redis.call('HGET', KEYS[1], 'readyKey')
            local active = redis.call('HGET', KEYS[1], 'activeScheduleKey')
            if ARGV[4] == '1' then
                if ready then redis.call('ZADD', ready, ARGV[3], ARGV[1]) end
                if active then redis.call('SADD', active, ARGV[1]) end
            else
                local completed = redis.call('HGET', KEYS[1], 'completedUtc')
                if completed then redis.call('ZADD', KEYS[5], completed, ARGV[1]) end
            end
            refreshBrokerHistory(KEYS[1], tonumber(ARGV[6]))
            syncMonitoring(KEYS[1])
            return 1
            """;
        var args = new List<RedisValue> { state.JobId, _options.MaxActiveJobs, Ticks(state.Status == JobStatus.Processing ? state.LeaseExpiresUtc ?? now : state.AvailableUtc ?? state.CreatedUtc), state.Status is JobStatus.Queued or JobStatus.Scheduled or JobStatus.Processing or JobStatus.RetryPending or JobStatus.EnqueueUnknown ? "1" : "0", _options.MaxDeduplicationRecords, Ticks(now), state.CompletedUtc is { } completed ? Ticks(completed.Add(_options.DeduplicationRetention)) : "+inf" };
        args.Add(_prefix);
        foreach (var field in ToHash(state)) { args.Add(field.Name); args.Add(field.Value); }
        RedisKey[] keys = [JobKey(state.JobId), AllKey, StatusKey(state.Status), NameKey(state.Name), TerminalKey, DeduplicationKey, UnclaimedKey];
        var values = args.ToArray();
        long result;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = (long)await _db.ScriptEvaluateAsync(script, keys, values).WaitAsync(cancellationToken).ConfigureAwait(false);
        } while (result == -3);
        ThrowIfCapacityExceeded(result);
    }

    public async Task<JobState?> GetAsync(string jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const string script = MonitoringFunctions + "\n" + """
            if expireJob(KEYS[1], tonumber(ARGV[1])) then return {} end
            return redis.call('HGETALL', KEYS[1])
            """;
        var snapshot = await _db.ScriptEvaluateAsync(script, [JobKey(jobId)], [Ticks(_timeProvider.GetUtcNow())]).WaitAsync(cancellationToken).ConfigureAwait(false);
        return ((RedisResult[])snapshot!).Length == 0 ? null : ReadJobSnapshot(snapshot);
    }

    public async Task<JobPage> QueryAsync(JobQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        await PurgeBrokerHistoryAsync(cancellationToken).ConfigureAwait(false);
        if (query.NewestFirst) return await QueryNewestAsync(query, cancellationToken).ConfigureAwait(false);
        var index = query.Name is not null ? NameKey(query.Name) : query.Status is { } status ? StatusKey(status) : AllKey;
        const string script = """
            local ids = redis.call('ZRANGEBYLEX', KEYS[1], ARGV[1], '+', 'LIMIT', 0, 1001)
            local result, cursor = {}, ''
            for i = 1, math.min(#ids, 1000) do
                local id = ids[i]
                local job = ARGV[2] .. id
                if (ARGV[3] == '' or redis.call('HGET', job, 'status') == ARGV[3]) and (ARGV[5] == '' or redis.call('HGET', job, 'queueName') == ARGV[5]) then
                    table.insert(result, redis.call('HGETALL', job))
                end
                if #result >= tonumber(ARGV[4]) or i == 1000 then
                    if i < #ids then cursor = id end
                    break
                end
            end
            return {cursor, result}
            """;
        var raw = (RedisResult[])(await _db.ScriptEvaluateAsync(script, new RedisKey[] { index }, new RedisValue[] { query.AfterJobId is null ? "-" : "(" + query.AfterJobId, $"{_prefix}job:", query.Status?.ToString() ?? "", query.Limit, query.QueueName ?? "" }).ConfigureAwait(false))!;
        var states = ((RedisResult[])raw[1]!).Select(ReadJobSnapshot).ToArray();
        string? cursor = (string?)raw[0];
        return new JobPage(states, String.IsNullOrEmpty(cursor) ? null : cursor);
    }

    private static JobState ReadJobSnapshot(RedisResult snapshot)
    {
        var values = (RedisResult[])snapshot!;
        var fields = new Dictionary<RedisValue, RedisValue>(values.Length / 2);
        for (int i = 0; i < values.Length; i += 2)
            fields.Add((RedisValue)values[i], (RedisValue)values[i + 1]);
        return FromHash(fields);
    }

    private const string RetentionFunctions = MonitoringFunctions + "\n" + """
        local function trimHistory(prefix, now, maximum, retention, limit)
            if limit <= 0 then return 0 end
            local terminal = prefix .. 'terminal'
            local count = redis.call('ZCARD', terminal)
            local removeCount = math.min(limit, math.max(count - maximum, redis.call('ZCOUNT', terminal, '-inf', now - retention)))
            if removeCount <= 0 then return 0 end
            local candidates = redis.call('ZRANGE', terminal, 0, removeCount - 1)
            for _, id in ipairs(candidates) do
                local job = prefix .. 'job:' .. id
                forgetJob(job, prefix, id)
            end
            return #candidates
        end
        local function finishJob(job, id, prefix, now, maximum, retention, dedupRetention)
            if redis.call('HGET', job, 'executionOwner') ~= 'Broker' then
                redis.call('ZADD', prefix .. 'deduplication', string.format('%.0f', now + dedupRetention), id)
            end
            redis.call('ZREM', prefix .. 'unclaimed', id)
            return trimHistory(prefix, now, maximum, retention, 1000)
        end
        """;

    private void ValidatePayload(long bytes)
    {
        if (bytes > _options.MaxPayloadBytes)
            throw new JobException($"Payload exceeds the configured {_options.MaxPayloadBytes} byte limit.");
    }

    private void ThrowIfCapacityExceeded(long result)
    {
        if (result is -1 or -2)
            throw new JobException(result == -1
                ? $"Active job capacity ({_options.MaxActiveJobs}) reached. Configure RedisJobRuntimeStoreOptions.Runtime.MaxActiveJobs."
                : $"Idempotency capacity ({_options.MaxDeduplicationRecords}) reached. Configure RedisJobRuntimeStoreOptions.Runtime.MaxDeduplicationRecords.");
    }

    public async Task<JobRuntimeStoreStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await PurgeBrokerHistoryAsync(cancellationToken).ConfigureAwait(false);
        var raw = (RedisResult[])(await _db.ScriptEvaluateAsync("return {redis.call('ZCARD', KEYS[1]), redis.call('ZCARD', KEYS[2]), redis.call('ZCARD', KEYS[3]), redis.call('ZCARD', KEYS[4])}",
            [AllKey, TerminalKey, DeduplicationKey, DueKey]).ConfigureAwait(false))!;
        return new JobRuntimeStoreStats((long)raw[0] - (long)raw[1], (long)raw[1], (long)raw[2], (long)raw[3]);
    }

    public async Task<int> CleanupAsync(int limit = 1000, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1000);
        cancellationToken.ThrowIfCancellationRequested();
        await PurgeBrokerHistoryAsync(cancellationToken).ConfigureAwait(false);
        const string script = RetentionFunctions + "\n" + """
            local now, prefix = tonumber(ARGV[1]), ARGV[3]
            redis.call('ZREMRANGEBYSCORE', KEYS[3], '-inf', ARGV[1])
            local expired = redis.call('ZRANGEBYSCORE', KEYS[4], '-inf', ARGV[1], 'LIMIT', 0, ARGV[2])
            local retired = 0
            for _, id in ipairs(expired) do
                local job = prefix .. 'job:' .. id
                local status = redis.call('HGET', job, 'status')
                if (status == 'Queued' or status == 'Scheduled') and redis.call('HGET', job, 'attempt') == '0' then
                    redis.call('ZREM', prefix .. 'status:' .. status, id)
                    redis.call('ZADD', prefix .. 'status:Cancelled', 0, id)
                    redis.call('HSET', job, 'status', 'Cancelled', 'completedUtc', ARGV[1], 'lastUpdatedUtc', ARGV[1], 'resultMessage', 'Unclaimed per-node occurrence expired.')
                    redis.call('ZADD', KEYS[1], ARGV[1], id)
                    syncMonitoring(job)
                    local ready, active = redis.call('HGET', job, 'readyKey'), redis.call('HGET', job, 'activeScheduleKey')
                    if ready then redis.call('ZREM', ready, id) end
                    if active then redis.call('SREM', active, id) end
                    redis.call('ZADD', KEYS[3], string.format('%.0f', now + tonumber(ARGV[6])), id)
                    retired = retired + 1
                end
                redis.call('ZREM', KEYS[4], id)
            end
            return retired + trimHistory(prefix, now, tonumber(ARGV[4]), tonumber(ARGV[5]), tonumber(ARGV[2]) - retired)
            """;
        return (int)(await _db.ScriptEvaluateAsync(script, [TerminalKey, AllKey, DeduplicationKey, UnclaimedKey],
            [Ticks(_timeProvider.GetUtcNow()), limit, _prefix, _options.MaxHistoryJobs, _options.HistoryRetention.Ticks, _options.DeduplicationRetention.Ticks]).ConfigureAwait(false));
    }

    public async Task<bool> RequestCancellationAsync(string jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await PurgeBrokerHistoryAsync(cancellationToken).ConfigureAwait(false);
        const string script = RetentionFunctions + "\n" + """
            if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
            local status = redis.call('HGET', KEYS[1], 'status')
            if redis.call('HGET', KEYS[1], 'executionOwner') == 'Broker' and (status == 'Completed' or status == 'Failed' or status == 'Cancelled' or status == 'DeadLettered') then return 0 end
            redis.call('HSET', KEYS[1], 'cancellationRequested', '1', 'lastUpdatedUtc', ARGV[2])
            if redis.call('HGET', KEYS[1], 'executionOwner') ~= 'Broker' and (status == 'Queued' or status == 'Scheduled') then
                redis.call('HSET', KEYS[1], 'status', 'Cancelled', 'completedUtc', ARGV[2])
                redis.call('ZREM', KEYS[2], ARGV[1])
                redis.call('ZREM', KEYS[3], ARGV[1])
                redis.call('ZADD', KEYS[4], 0, ARGV[1])
                redis.call('ZADD', KEYS[5], ARGV[2], ARGV[1])
                local ready = redis.call('HGET', KEYS[1], 'readyKey')
                local active = redis.call('HGET', KEYS[1], 'activeScheduleKey')
                if ready then redis.call('ZREM', ready, ARGV[1]) end
                if active then redis.call('SREM', active, ARGV[1]) end
                finishJob(KEYS[1], ARGV[1], ARGV[3], tonumber(ARGV[2]), tonumber(ARGV[4]), tonumber(ARGV[5]), tonumber(ARGV[6]))
            end
            syncMonitoring(KEYS[1])
            return 1
            """;
        var result = await _db.ScriptEvaluateAsync(script,
            new RedisKey[] { JobKey(jobId), StatusKey(JobStatus.Queued), StatusKey(JobStatus.Scheduled), StatusKey(JobStatus.Cancelled), TerminalKey },
            new RedisValue[] { jobId, Ticks(_timeProvider.GetUtcNow()), _prefix, _options.MaxHistoryJobs, _options.HistoryRetention.Ticks, _options.DeduplicationRetention.Ticks }).ConfigureAwait(false);
        return (long)result == 1;
    }

    public async Task<bool> IsCancellationRequestedAsync(string jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await PurgeBrokerHistoryAsync(cancellationToken).ConfigureAwait(false);
        var value = await _db.HashGetAsync(JobKey(jobId), "cancellationRequested").ConfigureAwait(false);
        return value == "1";
    }

    public async Task ScheduleDispatchAsync(ScheduledDispatchState dispatch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePayload(dispatch.Body.Length + dispatch.Headers.Sum(h => (long)System.Text.Encoding.UTF8.GetByteCount(h.Key) + System.Text.Encoding.UTF8.GetByteCount(h.Value)));
        const string script = """
            if redis.call('EXISTS', KEYS[1]) == 1 then return 0 end
            if redis.call('ZCARD', KEYS[2]) >= tonumber(ARGV[3]) then return -1 end
            redis.call('HSET', KEYS[1], unpack(ARGV, 4))
            redis.call('ZADD', KEYS[2], ARGV[2], ARGV[1])
            return 1
            """;
        var arguments = new List<RedisValue> { dispatch.DispatchId, Ticks(dispatch.DueUtc), _options.MaxScheduledDispatches };
        foreach (var field in ToHash(dispatch)) { arguments.Add(field.Name); arguments.Add(field.Value); }
        if ((long)await _db.ScriptEvaluateAsync(script, [DispatchKey(dispatch.DispatchId), DueKey], arguments.ToArray()).ConfigureAwait(false) == -1)
            throw new JobException($"Scheduled dispatch capacity ({_options.MaxScheduledDispatches}) reached.");
    }

    public async Task<IReadOnlyList<ScheduledDispatchState>> ClaimDueDispatchesAsync(DateTimeOffset now, int limit, string nodeId, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(nodeId);

        var result = await _db.ScriptEvaluateAsync(ClaimDueScript,
            [DueKey],
            [now.UtcTicks, Math.Max(1, limit), nodeId, Ticks(_timeProvider.GetUtcNow().Add(lease)), $"{_prefix}dispatch:"]).ConfigureAwait(false);

        var snapshots = (RedisResult[]?)result ?? [];
        var dispatches = new List<ScheduledDispatchState>(snapshots.Length);
        foreach (var snapshot in snapshots)
        {
            var values = (RedisValue[]?)snapshot ?? [];
            var entries = new HashEntry[values.Length / 2];
            for (int index = 0; index < entries.Length; index++)
                entries[index] = new HashEntry(values[index * 2], values[index * 2 + 1]);
            dispatches.Add(DispatchFromHash(entries));
        }

        return dispatches;
    }

    public async Task<bool> CompleteDispatchAsync(string dispatchId, string nodeId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (int)await _db.ScriptEvaluateAsync("""
            if redis.call('HGET', KEYS[1], 'claimOwner') ~= ARGV[1] then return 0 end
            if tonumber(redis.call('HGET', KEYS[1], 'claimExpiresUtc') or '0') <= tonumber(ARGV[2]) then return 0 end
            redis.call('DEL', KEYS[1])
            redis.call('ZREM', KEYS[2], ARGV[3])
            return 1
            """, [DispatchKey(dispatchId), DueKey], [nodeId, Ticks(_timeProvider.GetUtcNow()), dispatchId]).ConfigureAwait(false) == 1;
    }

    public Task ReleaseDispatchAsync(string dispatchId, string nodeId, DateTimeOffset nextDueUtc, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _db.ScriptEvaluateAsync("""
            if redis.call('HGET', KEYS[1], 'claimOwner') ~= ARGV[1] then return 0 end
            if tonumber(redis.call('HGET', KEYS[1], 'claimExpiresUtc') or '0') <= tonumber(ARGV[2]) then return 0 end
            redis.call('HDEL', KEYS[1], 'claimOwner', 'claimExpiresUtc')
            redis.call('HSET', KEYS[1], 'dueUtc', ARGV[4])
            redis.call('ZADD', KEYS[2], ARGV[4], ARGV[3])
            return 1
            """, [DispatchKey(dispatchId), DueKey], [nodeId, Ticks(_timeProvider.GetUtcNow()), dispatchId, Ticks(nextDueUtc)]);
    }

    private RedisKey JobKey(string id) => $"{_prefix}job:{id}";
    private RedisKey StatusKey(JobStatus status) => $"{_prefix}status:{status}";
    private RedisKey NameKey(string name) => $"{_prefix}name:{name}";
    private RedisKey DispatchKey(string id) => $"{_prefix}dispatch:{id}";
    private RedisKey TerminalKey => $"{_prefix}terminal";
    private RedisKey AllKey => $"{_prefix}all";
    private RedisKey DeduplicationKey => $"{_prefix}deduplication";
    private RedisKey UnclaimedKey => $"{_prefix}unclaimed";
    private RedisKey DueKey => $"{_prefix}dispatches:due";

    private static string Ticks(DateTimeOffset value) => value.UtcTicks.ToString(CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseTime(RedisValue value)
    {
        return value.IsNullOrEmpty || !Int64.TryParse((string?)value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks)
            ? null
            : new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    private static string? ToStringOrNull(RedisValue value) => value.IsNullOrEmpty ? null : (string)value!;

    private HashEntry[] ToHash(JobState state)
    {
        var entries = new List<HashEntry>
        {
            new("jobId", state.JobId),
            new("name", state.Name),
            new("monitorName", EncodeKey(state.Name)),
            new("executionOwner", state.ExecutionOwner.ToString()),
            new("status", state.Status.ToString()),
            new("attempt", state.Attempt),
            new("maxAttempts", state.MaxAttempts),
            new("retryPolicy", JsonSerializer.Serialize(state.RetryPolicy)),
            new("retryInitialSeconds", state.RetryPolicy.InitialDelay.TotalSeconds),
            new("retryMaxSeconds", state.RetryPolicy.MaxDelay.TotalSeconds),
            new("retryMultiplier", state.RetryPolicy.Multiplier),
            new("retryJitter", state.RetryPolicy.JitterFactor),
            new("cancellationRequested", state.CancellationRequested ? "1" : "0"),
            new("createdUtc", Ticks(state.CreatedUtc)),
            new("lastUpdatedUtc", Ticks(state.LastUpdatedUtc))
        };

        if (state.QueueName is not null)
        {
            entries.Add(new("queueName", state.QueueName));
            entries.Add(new("monitorQueue", EncodeKey(state.QueueName)));
        }
        if (state.Metadata is not null) entries.Add(new("metadata", JsonSerializer.Serialize(state.Metadata)));
        if (state.LastHeartbeatUtc is { } heartbeat) entries.Add(new("lastHeartbeatUtc", Ticks(heartbeat)));
        if (state.HistoryRetention is { } retention) entries.Add(new("historyRetention", retention.Ticks));
        if (state.HistoryExpiresUtc is { } historyExpiry) entries.Add(new("historyExpiresUtc", Ticks(historyExpiry)));
        if (state.JobType is not null)
        {
            entries.Add(new("jobType", state.JobType));
            if (state.ExecutionOwner == JobExecutionOwner.Runtime) entries.Add(new("readyKey", ReadyKey(state.JobType, state.RequiredNodeId).ToString()));
        }
        if (state.ExpiresUtc is { } expires) entries.Add(new("expiresUtc", Ticks(expires)));
        if (state.RequiredNodeId is not null) entries.Add(new("requiredNodeId", state.RequiredNodeId));
        if (state.ScheduleName is not null)
        {
            entries.Add(new("scheduleName", state.ScheduleName));
            entries.Add(new("activeScheduleKey", ActiveScheduleKey(state.ScheduleName, state.RequiredNodeId).ToString()));
        }
        if (state.Payload is { } payload) entries.Add(new("payload", Convert.ToBase64String(payload.Span)));
        if (state.PayloadType is not null) entries.Add(new("payloadType", state.PayloadType));
        if (state.Progress is { } progress) entries.Add(new("progress", progress));
        if (state.ProgressMessage is not null) entries.Add(new("progressMessage", state.ProgressMessage));
        if (state.NodeId is not null) entries.Add(new("nodeId", state.NodeId));
        if (state.ClaimToken is not null) entries.Add(new("claimToken", state.ClaimToken));
        if (state.AvailableUtc is { } available) entries.Add(new("availableUtc", Ticks(available)));
        if (state.StartedUtc is { } started) entries.Add(new("startedUtc", Ticks(started)));
        if (state.CompletedUtc is { } completed) entries.Add(new("completedUtc", Ticks(completed)));
        if (state.LeaseExpiresUtc is { } leaseExpires) entries.Add(new("leaseExpiresUtc", Ticks(leaseExpires)));
        if (state.Error is not null) entries.Add(new("error", state.Error));
        if (state.ScheduledForUtc is { } scheduledFor) entries.Add(new("scheduledForUtc", Ticks(scheduledFor)));

        return entries.ToArray();
    }

    private static JobState FromHash(Dictionary<RedisValue, RedisValue> map)
    {
        RedisValue Get(string field) => map.TryGetValue(field, out var value) ? value : RedisValue.Null;

        return new JobState
        {
            JobId = (string)Get("jobId")!,
            Name = (string)Get("name")!,
            JobType = ToStringOrNull(Get("jobType")),
            ExecutionOwner = Get("executionOwner") == "Broker" ? JobExecutionOwner.Broker : JobExecutionOwner.Runtime,
            QueueName = ToStringOrNull(Get("queueName")),
            Metadata = Get("metadata").IsNullOrEmpty ? null : JsonSerializer.Deserialize<Dictionary<string, string>>((string)Get("metadata")!),
            LastHeartbeatUtc = ParseTime(Get("lastHeartbeatUtc")),
            HistoryRetention = Get("historyRetention").IsNullOrEmpty ? null : TimeSpan.FromTicks((long)Get("historyRetention")),
            HistoryExpiresUtc = ParseTime(Get("historyExpiresUtc")),
            Payload = Get("payload").IsNullOrEmpty ? null : Convert.FromBase64String((string)Get("payload")!),
            PayloadType = ToStringOrNull(Get("payloadType")),
            Status = Enum.Parse<JobStatus>((string)Get("status")!),
            Progress = Get("progress").IsNullOrEmpty ? null : (int)Get("progress"),
            ProgressMessage = ToStringOrNull(Get("progressMessage")),
            Attempt = Get("attempt").IsNullOrEmpty ? 0 : (int)Get("attempt"),
            NodeId = ToStringOrNull(Get("nodeId")),
            ClaimToken = ToStringOrNull(Get("claimToken")),
            RequiredNodeId = ToStringOrNull(Get("requiredNodeId")),
            ScheduleName = ToStringOrNull(Get("scheduleName")),
            MaxAttempts = Get("maxAttempts").IsNullOrEmpty ? 3 : (int)Get("maxAttempts"),
            AvailableUtc = ParseTime(Get("availableUtc")),
            ExpiresUtc = ParseTime(Get("expiresUtc")),
            RetryPolicy = Get("retryPolicy").IsNullOrEmpty ? new() : JsonSerializer.Deserialize<JobRetryPolicy>((string)Get("retryPolicy")!)!,
            CreatedUtc = ParseTime(Get("createdUtc")) ?? default,
            LastUpdatedUtc = ParseTime(Get("lastUpdatedUtc")) ?? default,
            StartedUtc = ParseTime(Get("startedUtc")),
            CompletedUtc = ParseTime(Get("completedUtc")),
            LeaseExpiresUtc = ParseTime(Get("leaseExpiresUtc")),
            Error = ToStringOrNull(Get("error")),
            ResultMessage = ToStringOrNull(Get("resultMessage")),
            CancellationRequested = Get("cancellationRequested") == "1",
            ScheduledForUtc = ParseTime(Get("scheduledForUtc"))
        };
    }

    private static HashEntry[] ToHash(ScheduledDispatchState dispatch)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in dispatch.Headers)
            headers[header.Key] = header.Value;

        var entries = new List<HashEntry>
        {
            new("dispatchId", dispatch.DispatchId),
            new("kind", dispatch.Kind.ToString()),
            new("body", Convert.ToBase64String(dispatch.Body.Span)),
            new("headers", JsonSerializer.Serialize(headers)),
            new("options", JsonSerializer.Serialize(dispatch.Options)),
            new("dueUtc", Ticks(dispatch.DueUtc)),
            new("attempts", dispatch.Attempts)
        };

        if (dispatch.Destination is not null) entries.Add(new("destination", JsonSerializer.Serialize(dispatch.Destination)));
        if (dispatch.ClaimOwner is not null) entries.Add(new("claimOwner", dispatch.ClaimOwner));
        if (dispatch.ClaimExpiresUtc is { } claimExpires) entries.Add(new("claimExpiresUtc", Ticks(claimExpires)));

        return entries.ToArray();
    }

    private static ScheduledDispatchState DispatchFromHash(HashEntry[] entries)
    {
        var map = entries.ToDictionary(e => (string)e.Name!, e => e.Value);
        RedisValue Get(string field) => map.TryGetValue(field, out var value) ? value : RedisValue.Null;

        string headersJson = (string?)Get("headers") ?? "{}";
        var headerMap = JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson) ?? [];
        var options = JsonSerializer.Deserialize<TransportSendOptions>((string?)Get("options") ?? "{}") ?? new TransportSendOptions();
        var destination = Get("destination");

        return new ScheduledDispatchState
        {
            DispatchId = (string)Get("dispatchId")!,
            Kind = Enum.Parse<ScheduledDispatchKind>((string)Get("kind")!),
            Destination = destination.IsNullOrEmpty ? null : JsonSerializer.Deserialize<DestinationAddress>((string)destination!),
            Body = Get("body").IsNullOrEmpty ? ReadOnlyMemory<byte>.Empty : Convert.FromBase64String((string)Get("body")!),
            Headers = MessageHeaders.Create(headerMap),
            Options = options,
            DueUtc = ParseTime(Get("dueUtc")) ?? default,
            ClaimOwner = ToStringOrNull(Get("claimOwner")),
            ClaimExpiresUtc = ParseTime(Get("claimExpiresUtc")),
            Attempts = Get("attempts").IsNullOrEmpty ? 0 : (int)Get("attempts")
        };
    }
}

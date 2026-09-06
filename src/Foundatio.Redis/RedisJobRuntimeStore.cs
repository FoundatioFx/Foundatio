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
    private readonly int _maxJobs;

    public RedisJobRuntimeStore(RedisJobRuntimeStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.ConnectionMultiplexer);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxJobs, 1);
        _maxJobs = options.MaxJobs;
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
        var now = _timeProvider.GetUtcNow();
        var state = initial with { CreatedUtc = initial.CreatedUtc == default ? now : initial.CreatedUtc, LastUpdatedUtc = initial.LastUpdatedUtc == default ? now : initial.LastUpdatedUtc };
        const string script = """
            if redis.call('EXISTS', KEYS[1]) == 1 then return 0 end
            if redis.call('ZCARD', KEYS[2]) >= tonumber(ARGV[2]) then return -1 end
            redis.call('HSET', KEYS[1], unpack(ARGV, 5))
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
            return 1
            """;
        var args = new List<RedisValue> { state.JobId, _maxJobs, Ticks(state.Status == JobStatus.Processing ? state.LeaseExpiresUtc ?? now : state.AvailableUtc ?? state.CreatedUtc), state.Status is JobStatus.Queued or JobStatus.Scheduled or JobStatus.Processing ? "1" : "0" };
        foreach (var field in ToHash(state)) { args.Add(field.Name); args.Add(field.Value); }
        var result = await _db.ScriptEvaluateAsync(script, new RedisKey[] { JobKey(state.JobId), AllKey, StatusKey(state.Status), NameKey(state.Name), TerminalKey }, args.ToArray()).ConfigureAwait(false);
        if ((long)result == -1) throw new JobException($"Job storage capacity ({_maxJobs}) reached. Run cleanup or increase capacity before enqueueing more work.");
    }

    public async Task<JobState?> GetAsync(string jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = await _db.HashGetAllAsync(JobKey(jobId)).ConfigureAwait(false);
        return entries.Length == 0 ? null : FromHash(entries);
    }

    public async Task<JobPage> QueryAsync(JobQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var index = query.Name is not null ? NameKey(query.Name) : query.Status is { } status ? StatusKey(status) : AllKey;
        const string script = """
            local ids = redis.call('ZRANGEBYLEX', KEYS[1], ARGV[1], '+', 'LIMIT', 0, 1001)
            local result, cursor = {}, ''
            for i = 1, math.min(#ids, 1000) do
                local id = ids[i]
                local job = ARGV[2] .. id
                if ARGV[3] == '' or redis.call('HGET', job, 'status') == ARGV[3] then
                    table.insert(result, redis.call('HGETALL', job))
                end
                if #result >= tonumber(ARGV[4]) or i == 1000 then
                    if i < #ids then cursor = id end
                    break
                end
            end
            return {cursor, result}
            """;
        var raw = (RedisResult[])(await _db.ScriptEvaluateAsync(script, new RedisKey[] { index }, new RedisValue[] { query.AfterJobId is null ? "-" : "(" + query.AfterJobId, $"{_prefix}job:", query.Status?.ToString() ?? "", query.Limit }).ConfigureAwait(false))!;
        var states = ((RedisResult[])raw[1]!).Select(ReadJobSnapshot).ToArray();
        string? cursor = (string?)raw[0];
        return new JobPage(states, String.IsNullOrEmpty(cursor) ? null : cursor);
    }

    private static JobState ReadJobSnapshot(RedisResult snapshot)
    {
        var values = (RedisValue[])snapshot!;
        var fields = new HashEntry[values.Length / 2];
        for (int i = 0; i < fields.Length; i++) fields[i] = new HashEntry(values[i * 2], values[i * 2 + 1]);
        return FromHash(fields);
    }

    public async Task<int> CleanupAsync(int limit = 1000, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1000);
        cancellationToken.ThrowIfCancellationRequested();
        const string script = """
            local ids = redis.call('ZRANGEBYSCORE', KEYS[1], '-inf', ARGV[1], 'LIMIT', 0, ARGV[2])
            for _, id in ipairs(ids) do
                local job = ARGV[3] .. 'job:' .. id
                local status = redis.call('HGET', job, 'status')
                local name = redis.call('HGET', job, 'name')
                if status then redis.call('ZREM', ARGV[3] .. 'status:' .. status, id) end
                if name then redis.call('ZREM', ARGV[3] .. 'name:' .. name, id) end
                redis.call('ZREM', KEYS[2], id)
                redis.call('ZREM', KEYS[1], id)
                redis.call('DEL', job)
            end
            return #ids
            """;
        return (int)(await _db.ScriptEvaluateAsync(script, new RedisKey[] { TerminalKey, AllKey }, new RedisValue[] { Ticks(_timeProvider.GetUtcNow().AddDays(-7)), limit, _prefix }).ConfigureAwait(false));
    }

    public async Task<bool> RequestCancellationAsync(string jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const string script = """
            if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
            local status = redis.call('HGET', KEYS[1], 'status')
            redis.call('HSET', KEYS[1], 'cancellationRequested', '1', 'lastUpdatedUtc', ARGV[2])
            if status == 'Queued' or status == 'Scheduled' then
                redis.call('HSET', KEYS[1], 'status', 'Cancelled', 'completedUtc', ARGV[2])
                redis.call('ZREM', KEYS[2], ARGV[1])
                redis.call('ZREM', KEYS[3], ARGV[1])
                redis.call('ZADD', KEYS[4], 0, ARGV[1])
                redis.call('ZADD', KEYS[5], ARGV[2], ARGV[1])
                local ready = redis.call('HGET', KEYS[1], 'readyKey')
                local active = redis.call('HGET', KEYS[1], 'activeScheduleKey')
                if ready then redis.call('ZREM', ready, ARGV[1]) end
                if active then redis.call('SREM', active, ARGV[1]) end
            end
            return 1
            """;
        var result = await _db.ScriptEvaluateAsync(script,
            new RedisKey[] { JobKey(jobId), StatusKey(JobStatus.Queued), StatusKey(JobStatus.Scheduled), StatusKey(JobStatus.Cancelled), TerminalKey },
            new RedisValue[] { jobId, Ticks(_timeProvider.GetUtcNow()) }).ConfigureAwait(false);
        return (long)result == 1;
    }

    public async Task<bool> IsCancellationRequestedAsync(string jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = await _db.HashGetAsync(JobKey(jobId), "cancellationRequested").ConfigureAwait(false);
        return value == "1";
    }

    public Task ScheduleDispatchAsync(ScheduledDispatchState dispatch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        cancellationToken.ThrowIfCancellationRequested();

        var tx = _db.CreateTransaction();
        tx.AddCondition(Condition.KeyNotExists(DispatchKey(dispatch.DispatchId)));
        _ = tx.HashSetAsync(DispatchKey(dispatch.DispatchId), ToHash(dispatch));
        _ = tx.SortedSetAddAsync(DueKey, dispatch.DispatchId, dispatch.DueUtc.UtcTicks);
        return tx.ExecuteAsync(); // result ignored: false => already scheduled; no-op
    }

    public async Task<IReadOnlyList<ScheduledDispatchState>> ClaimDueDispatchesAsync(DateTimeOffset now, int limit, string nodeId, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(nodeId);

        var result = await _db.ScriptEvaluateAsync(ClaimDueScript,
            [DueKey],
            [now.UtcTicks, Math.Max(1, limit), nodeId, Ticks(now.Add(lease)), $"{_prefix}dispatch:"]).ConfigureAwait(false);

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

    public Task CompleteDispatchAsync(string dispatchId, string nodeId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _db.ScriptEvaluateAsync("""
            if redis.call('HGET', KEYS[1], 'claimOwner') ~= ARGV[1] then return 0 end
            if tonumber(redis.call('HGET', KEYS[1], 'claimExpiresUtc') or '0') <= tonumber(ARGV[2]) then return 0 end
            redis.call('DEL', KEYS[1])
            redis.call('ZREM', KEYS[2], ARGV[3])
            return 1
            """, [DispatchKey(dispatchId), DueKey], [nodeId, Ticks(_timeProvider.GetUtcNow()), dispatchId]);
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
            new("status", state.Status.ToString()),
            new("attempt", state.Attempt),
            new("maxAttempts", state.MaxAttempts),
            new("cancellationRequested", state.CancellationRequested ? "1" : "0"),
            new("createdUtc", Ticks(state.CreatedUtc)),
            new("lastUpdatedUtc", Ticks(state.LastUpdatedUtc))
        };

        if (state.JobType is not null)
        {
            entries.Add(new("jobType", state.JobType));
            entries.Add(new("readyKey", ReadyKey(state.JobType, state.RequiredNodeId).ToString()));
        }
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

    private static JobState FromHash(HashEntry[] entries)
    {
        var map = entries.ToDictionary(e => (string)e.Name!, e => e.Value);
        RedisValue Get(string field) => map.TryGetValue(field, out var value) ? value : RedisValue.Null;

        return new JobState
        {
            JobId = (string)Get("jobId")!,
            Name = (string)Get("name")!,
            JobType = ToStringOrNull(Get("jobType")),
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
            CreatedUtc = ParseTime(Get("createdUtc")) ?? default,
            LastUpdatedUtc = ParseTime(Get("lastUpdatedUtc")) ?? default,
            StartedUtc = ParseTime(Get("startedUtc")),
            CompletedUtc = ParseTime(Get("completedUtc")),
            LeaseExpiresUtc = ParseTime(Get("leaseExpiresUtc")),
            Error = ToStringOrNull(Get("error")),
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

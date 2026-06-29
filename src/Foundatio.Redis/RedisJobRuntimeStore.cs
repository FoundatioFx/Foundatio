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
/// A Redis-backed <see cref="IJobRuntimeStore"/>. Temporary in-repo provider used to validate the durable job runtime
/// (state transitions, leases/claims, scheduled dispatches) against a real distributed store.
/// </summary>
/// <remarks>
/// Job state is a hash at <c>{prefix}job:{id}</c>; status and name indexes are sets; due dispatches are a sorted set
/// scored by due time. Conditional transitions use Redis transactions with hash-field conditions (optimistic
/// concurrency), so a state change only commits if the fields it was predicated on are unchanged — including a
/// lease-value condition that makes reclaim safe against a concurrent renew. Times are stored as UTC ticks for
/// unambiguous numeric comparison.
/// </remarks>
public sealed class RedisJobRuntimeStore : IJobRuntimeStore
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
                    table.insert(claimed, id)
                end
            end
        end
        return claimed
        """;

    private readonly IDatabase _db;
    private readonly string _prefix;
    private readonly TimeProvider _timeProvider;

    public RedisJobRuntimeStore(RedisJobRuntimeStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.ConnectionMultiplexer);
        _db = options.ConnectionMultiplexer.GetDatabase();
        _prefix = options.KeyPrefix ?? "";
        _timeProvider = options.TimeProvider ?? TimeProvider.System;
    }

    public RedisJobRuntimeStore(IConnectionMultiplexer connectionMultiplexer, string keyPrefix = "fnd:jobs:", TimeProvider? timeProvider = null)
        : this(new RedisJobRuntimeStoreOptions { ConnectionMultiplexer = connectionMultiplexer, KeyPrefix = keyPrefix, TimeProvider = timeProvider }) { }

    public Task CreateIfAbsentAsync(JobState initial, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initial);
        cancellationToken.ThrowIfCancellationRequested();

        var now = _timeProvider.GetUtcNow();
        var state = initial with
        {
            CreatedUtc = initial.CreatedUtc == default ? now : initial.CreatedUtc,
            LastUpdatedUtc = initial.LastUpdatedUtc == default ? now : initial.LastUpdatedUtc
        };

        var tx = _db.CreateTransaction();
        tx.AddCondition(Condition.KeyNotExists(JobKey(state.JobId)));
        _ = tx.HashSetAsync(JobKey(state.JobId), ToHash(state));
        _ = tx.SetAddAsync(StatusKey(state.Status), state.JobId);
        _ = tx.SetAddAsync(NameKey(state.Name), state.JobId);
        _ = tx.SetAddAsync(AllKey, state.JobId);
        return tx.ExecuteAsync(); // result ignored: false => already present; create-if-absent is a no-op
    }

    public async Task<JobState?> GetAsync(string jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = await _db.HashGetAllAsync(JobKey(jobId)).ConfigureAwait(false);
        return entries.Length == 0 ? null : FromHash(entries);
    }

    public async Task<IReadOnlyList<JobState>> QueryAsync(JobQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        RedisValue[] ids;
        if (query.Status is { } status && !String.IsNullOrEmpty(query.Name))
            ids = await _db.SetCombineAsync(SetOperation.Intersect, StatusKey(status), NameKey(query.Name)).ConfigureAwait(false);
        else if (query.Status is { } onlyStatus)
            ids = await _db.SetMembersAsync(StatusKey(onlyStatus)).ConfigureAwait(false);
        else if (!String.IsNullOrEmpty(query.Name))
            ids = await _db.SetMembersAsync(NameKey(query.Name)).ConfigureAwait(false);
        else
            ids = await _db.SetMembersAsync(AllKey).ConfigureAwait(false);

        var states = await LoadAsync(ids).ConfigureAwait(false);
        return states
            .OrderByDescending(s => s.LastUpdatedUtc)
            .Take(Math.Max(1, query.Limit))
            .ToArray();
    }

    public Task<bool> TryTransitionAsync(string jobId, JobStatus expectedStatus, JobStatus newStatus, JobStatePatch? patch = null, string? expectedNodeId = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var tx = _db.CreateTransaction();
        tx.AddCondition(Condition.HashEqual(JobKey(jobId), "status", expectedStatus.ToString()));
        if (expectedNodeId is not null)
            tx.AddCondition(Condition.HashEqual(JobKey(jobId), "nodeId", expectedNodeId));

        ApplyTransition(tx, jobId, expectedStatus, newStatus, patch);
        return tx.ExecuteAsync();
    }

    public async Task<bool> TryClaimAsync(string jobId, string nodeId, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(nodeId);

        var now = _timeProvider.GetUtcNow();
        var current = await _db.HashGetAsync(JobKey(jobId), ["nodeId", "leaseExpiresUtc"]).ConfigureAwait(false);
        if (!await _db.KeyExistsAsync(JobKey(jobId)).ConfigureAwait(false))
            return false;

        string? owner = ToStringOrNull(current[0]);
        var leaseExpires = ParseTime(current[1]);
        bool heldByOther = !String.IsNullOrEmpty(owner) && owner != nodeId && leaseExpires is { } e && e > now;
        if (heldByOther)
            return false;

        var tx = _db.CreateTransaction();
        // Predicate on the owner we observed so a competing claim that lands first invalidates this one.
        tx.AddCondition(String.IsNullOrEmpty(owner) ? Condition.HashNotExists(JobKey(jobId), "nodeId") : Condition.HashEqual(JobKey(jobId), "nodeId", owner));
        _ = tx.HashSetAsync(JobKey(jobId),
        [
            new HashEntry("nodeId", nodeId),
            new HashEntry("leaseExpiresUtc", Ticks(now.Add(lease))),
            new HashEntry("lastUpdatedUtc", Ticks(now))
        ]);
        return await tx.ExecuteAsync().ConfigureAwait(false);
    }

    public Task<bool> RenewClaimAsync(string jobId, string nodeId, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = _timeProvider.GetUtcNow();

        var tx = _db.CreateTransaction();
        tx.AddCondition(Condition.HashEqual(JobKey(jobId), "nodeId", nodeId));
        _ = tx.HashSetAsync(JobKey(jobId),
        [
            new HashEntry("leaseExpiresUtc", Ticks(now.Add(lease))),
            new HashEntry("lastUpdatedUtc", Ticks(now))
        ]);
        return tx.ExecuteAsync();
    }

    public Task<bool> ReleaseClaimAsync(string jobId, string nodeId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var tx = _db.CreateTransaction();
        tx.AddCondition(Condition.HashEqual(JobKey(jobId), "nodeId", nodeId));
        _ = tx.HashDeleteAsync(JobKey(jobId), ["nodeId", "leaseExpiresUtc"]);
        _ = tx.HashSetAsync(JobKey(jobId), "lastUpdatedUtc", Ticks(_timeProvider.GetUtcNow()));
        return tx.ExecuteAsync();
    }

    public async Task<IReadOnlyList<JobState>> GetExpiredProcessingAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var ids = await _db.SetMembersAsync(StatusKey(JobStatus.Processing)).ConfigureAwait(false);
        var states = await LoadAsync(ids).ConfigureAwait(false);
        return states
            // Exclude CRON occurrences (ScheduledForUtc set): the scheduler owns their recovery.
            .Where(s => s.ScheduledForUtc is null && s.LeaseExpiresUtc is { } lease && lease <= now)
            .OrderBy(s => s.LeaseExpiresUtc)
            .Take(Math.Max(1, limit))
            .ToArray();
    }

    public async Task<bool> TryReclaimExpiredAsync(string jobId, DateTimeOffset now, string expectedNodeId, JobStatus newStatus, JobStatePatch? patch = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(expectedNodeId);

        var state = await GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (state is null || state.Status != JobStatus.Processing || !String.Equals(state.NodeId, expectedNodeId, StringComparison.Ordinal))
            return false;
        if (state.LeaseExpiresUtc is not { } lease || lease > now)
            return false;

        var tx = _db.CreateTransaction();
        tx.AddCondition(Condition.HashEqual(JobKey(jobId), "status", JobStatus.Processing.ToString()));
        tx.AddCondition(Condition.HashEqual(JobKey(jobId), "nodeId", expectedNodeId));
        // Predicate on the exact lease we read; a concurrent renew changes it and invalidates the reclaim.
        tx.AddCondition(Condition.HashEqual(JobKey(jobId), "leaseExpiresUtc", Ticks(lease)));

        ApplyTransition(tx, jobId, JobStatus.Processing, newStatus, patch);
        return await tx.ExecuteAsync().ConfigureAwait(false);
    }

    public Task SetProgressAsync(string jobId, int? percent = null, string? message = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var tx = _db.CreateTransaction();
        tx.AddCondition(Condition.KeyExists(JobKey(jobId)));
        if (percent is { } p)
            _ = tx.HashSetAsync(JobKey(jobId), "progress", p);
        if (message is not null)
            _ = tx.HashSetAsync(JobKey(jobId), "progressMessage", message);
        _ = tx.HashSetAsync(JobKey(jobId), "lastUpdatedUtc", Ticks(_timeProvider.GetUtcNow()));
        return tx.ExecuteAsync();
    }

    public Task IncrementAttemptAsync(string jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var tx = _db.CreateTransaction();
        tx.AddCondition(Condition.KeyExists(JobKey(jobId)));
        _ = tx.HashIncrementAsync(JobKey(jobId), "attempt", 1);
        _ = tx.HashSetAsync(JobKey(jobId), "lastUpdatedUtc", Ticks(_timeProvider.GetUtcNow()));
        return tx.ExecuteAsync();
    }

    public Task<bool> RequestCancellationAsync(string jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var tx = _db.CreateTransaction();
        tx.AddCondition(Condition.KeyExists(JobKey(jobId)));
        _ = tx.HashSetAsync(JobKey(jobId),
        [
            new HashEntry("cancellationRequested", "1"),
            new HashEntry("lastUpdatedUtc", Ticks(_timeProvider.GetUtcNow()))
        ]);
        return tx.ExecuteAsync();
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

        var ids = (RedisValue[]?)result ?? [];
        var dispatches = new List<ScheduledDispatchState>(ids.Length);
        foreach (var id in ids)
        {
            var entries = await _db.HashGetAllAsync(DispatchKey(id!)).ConfigureAwait(false);
            if (entries.Length > 0)
                dispatches.Add(DispatchFromHash(entries));
        }

        return dispatches;
    }

    public Task CompleteDispatchAsync(string dispatchId, string nodeId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var tx = _db.CreateTransaction();
        tx.AddCondition(Condition.HashEqual(DispatchKey(dispatchId), "claimOwner", nodeId));
        _ = tx.KeyDeleteAsync(DispatchKey(dispatchId));
        _ = tx.SortedSetRemoveAsync(DueKey, dispatchId);
        return tx.ExecuteAsync();
    }

    public Task ReleaseDispatchAsync(string dispatchId, string nodeId, DateTimeOffset nextDueUtc, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var tx = _db.CreateTransaction();
        tx.AddCondition(Condition.HashEqual(DispatchKey(dispatchId), "claimOwner", nodeId));
        _ = tx.HashDeleteAsync(DispatchKey(dispatchId), ["claimOwner", "claimExpiresUtc"]);
        _ = tx.HashSetAsync(DispatchKey(dispatchId), "dueUtc", Ticks(nextDueUtc));
        _ = tx.SortedSetAddAsync(DueKey, dispatchId, nextDueUtc.UtcTicks);
        return tx.ExecuteAsync();
    }

    private void ApplyTransition(ITransaction tx, string jobId, JobStatus fromStatus, JobStatus toStatus, JobStatePatch? patch)
    {
        var sets = new List<HashEntry>
        {
            new("status", toStatus.ToString()),
            new("lastUpdatedUtc", Ticks(patch?.LastUpdatedUtc ?? _timeProvider.GetUtcNow()))
        };
        var deletes = new List<RedisValue>();

        if (patch is not null)
        {
            if (patch.JobType is not null) sets.Add(new("jobType", patch.JobType));
            if (patch.Progress is { } progress) sets.Add(new("progress", progress));
            if (patch.ProgressMessage is not null) sets.Add(new("progressMessage", patch.ProgressMessage));
            if (patch.Error is not null) sets.Add(new("error", patch.Error));
            if (patch.StartedUtc is { } started) sets.Add(new("startedUtc", Ticks(started)));
            if (patch.CompletedUtc is { } completed) sets.Add(new("completedUtc", Ticks(completed)));
            if (patch.CancellationRequested is { } cancel) sets.Add(new("cancellationRequested", cancel ? "1" : "0"));

            if (patch.ClearNodeId) deletes.Add("nodeId");
            else if (patch.NodeId is not null) sets.Add(new("nodeId", patch.NodeId));

            if (patch.ClearLeaseExpiresUtc) deletes.Add("leaseExpiresUtc");
            else if (patch.LeaseExpiresUtc is { } leaseExpires) sets.Add(new("leaseExpiresUtc", Ticks(leaseExpires)));

            if (patch.AttemptDelta != 0)
                _ = tx.HashIncrementAsync(JobKey(jobId), "attempt", patch.AttemptDelta);
        }

        _ = tx.HashSetAsync(JobKey(jobId), sets.ToArray());
        if (deletes.Count > 0)
            _ = tx.HashDeleteAsync(JobKey(jobId), deletes.ToArray());

        if (fromStatus != toStatus)
        {
            _ = tx.SetRemoveAsync(StatusKey(fromStatus), jobId);
            _ = tx.SetAddAsync(StatusKey(toStatus), jobId);
        }
    }

    private async Task<List<JobState>> LoadAsync(RedisValue[] ids)
    {
        var states = new List<JobState>(ids.Length);
        foreach (var id in ids)
        {
            var entries = await _db.HashGetAllAsync(JobKey(id!)).ConfigureAwait(false);
            if (entries.Length > 0)
                states.Add(FromHash(entries));
        }

        return states;
    }

    private RedisKey JobKey(string id) => $"{_prefix}job:{id}";
    private RedisKey StatusKey(JobStatus status) => $"{_prefix}status:{status}";
    private RedisKey NameKey(string name) => $"{_prefix}name:{name}";
    private RedisKey DispatchKey(string id) => $"{_prefix}dispatch:{id}";
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

    private static HashEntry[] ToHash(JobState state)
    {
        var entries = new List<HashEntry>
        {
            new("jobId", state.JobId),
            new("name", state.Name),
            new("status", state.Status.ToString()),
            new("attempt", state.Attempt),
            new("cancellationRequested", state.CancellationRequested ? "1" : "0"),
            new("createdUtc", Ticks(state.CreatedUtc)),
            new("lastUpdatedUtc", Ticks(state.LastUpdatedUtc))
        };

        if (state.JobType is not null) entries.Add(new("jobType", state.JobType));
        if (state.Progress is { } progress) entries.Add(new("progress", progress));
        if (state.ProgressMessage is not null) entries.Add(new("progressMessage", state.ProgressMessage));
        if (state.NodeId is not null) entries.Add(new("nodeId", state.NodeId));
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
            Status = Enum.Parse<JobStatus>((string)Get("status")!),
            Progress = Get("progress").IsNullOrEmpty ? null : (int)Get("progress"),
            ProgressMessage = ToStringOrNull(Get("progressMessage")),
            Attempt = Get("attempt").IsNullOrEmpty ? 0 : (int)Get("attempt"),
            NodeId = ToStringOrNull(Get("nodeId")),
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
            new("destination", dispatch.Destination),
            new("body", Convert.ToBase64String(dispatch.Body.Span)),
            new("headers", JsonSerializer.Serialize(headers)),
            new("options", JsonSerializer.Serialize(dispatch.Options)),
            new("dueUtc", Ticks(dispatch.DueUtc)),
            new("attempts", dispatch.Attempts)
        };

        if (dispatch.ClaimOwner is not null) entries.Add(new("claimOwner", dispatch.ClaimOwner));
        if (dispatch.ClaimExpiresUtc is { } claimExpires) entries.Add(new("claimExpiresUtc", Ticks(claimExpires)));
        if (dispatch.JobId is not null) entries.Add(new("jobId", dispatch.JobId));

        return entries.ToArray();
    }

    private static ScheduledDispatchState DispatchFromHash(HashEntry[] entries)
    {
        var map = entries.ToDictionary(e => (string)e.Name!, e => e.Value);
        RedisValue Get(string field) => map.TryGetValue(field, out var value) ? value : RedisValue.Null;

        string headersJson = (string?)Get("headers") ?? "{}";
        var headerMap = JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson) ?? [];
        var options = JsonSerializer.Deserialize<TransportSendOptions>((string?)Get("options") ?? "{}") ?? new TransportSendOptions();

        return new ScheduledDispatchState
        {
            DispatchId = (string)Get("dispatchId")!,
            Kind = Enum.Parse<ScheduledDispatchKind>((string)Get("kind")!),
            Destination = (string)Get("destination")!,
            Body = Get("body").IsNullOrEmpty ? ReadOnlyMemory<byte>.Empty : Convert.FromBase64String((string)Get("body")!),
            Headers = MessageHeaders.Create(headerMap),
            Options = options,
            DueUtc = ParseTime(Get("dueUtc")) ?? default,
            ClaimOwner = ToStringOrNull(Get("claimOwner")),
            ClaimExpiresUtc = ParseTime(Get("claimExpiresUtc")),
            Attempts = Get("attempts").IsNullOrEmpty ? 0 : (int)Get("attempts"),
            JobId = ToStringOrNull(Get("jobId"))
        };
    }
}

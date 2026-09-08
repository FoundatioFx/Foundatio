using Foundatio.Jobs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using StackExchange.Redis;

namespace Foundatio.Messaging;

/// <summary>
/// Redis-backed implementation of <see cref="IMessageExecutionStore"/>.
/// Each job is stored as a Redis Hash. Per-queue and per-status sorted sets scored by creation time
/// index jobs for pagination. Cancellation uses a separate key that shares the job's TTL.
/// </summary>
/// <remarks>
/// Key layout (<c>{prefix}</c> is <see cref="RedisMessageExecutionStoreOptions.KeyPrefix"/>, optionally preceded by
/// <see cref="RedisMessageExecutionStoreOptions.ResourcePrefix"/>):
/// <list type="bullet">
/// <item><c>{prefix}:{jobId}</c> — hash of job fields; metadata is stored as <c>meta:{name}</c> fields</item>
/// <item><c>{prefix}:{jobId}:cancel</c> — cancellation flag</item>
/// <item><c>{prefix}:queues:{queueName}</c> — sorted set of every job in the queue</item>
/// <item><c>{prefix}:queues:{queueName}:status:{status}</c> — sorted set per <see cref="MessageExecutionStatus"/> value</item>
/// <item><c>{prefix}:counters:{queueName}:{yyyy-MM-ddTHH}</c> — hourly counter hash</item>
/// </list>
/// Writes update hashes, cancellation flags, and indexes atomically using server-side scripts.
/// A separate expiration index uses Redis server deadlines; creation time never implies expiration.
/// For Redis Cluster, configure a common hash tag in KeyPrefix so a store's keys share a slot.
/// </remarks>
public sealed class RedisMessageExecutionStore : IMessageExecutionStore
{
    /// <inheritdoc />
    public bool IsShared => true;

    private const string MetadataFieldPrefix = "meta:";
    private static readonly TimeSpan JobCounterBucketRetention = TimeSpan.FromHours(48);

    private readonly IConnectionMultiplexer _redis;
    private readonly RedisMessageExecutionStoreOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly string _keyPrefix;

    public RedisMessageExecutionStore(IConnectionMultiplexer redis, RedisMessageExecutionStoreOptions? options = null, TimeProvider? timeProvider = null)
    {
        _redis = redis;
        _options = options ?? new RedisMessageExecutionStoreOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _keyPrefix = string.IsNullOrEmpty(_options.ResourcePrefix)
            ? _options.KeyPrefix
            : $"{_options.ResourcePrefix}:{_options.KeyPrefix}";
    }

    public Task SetJobStateAsync(MessageExecutionState state, TimeSpan? expiry = null, CancellationToken cancellationToken = default)
        => MutateAsync("set", state.JobId, ResolveTtl(expiry, state.Status),
            BuildEntries(state, state.CreatedUtc.ToUnixTimeMilliseconds()), cancellationToken);

    public async Task<MessageExecutionState?> GetJobStateAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var entries = await _redis.GetDatabase().HashGetAllAsync(JobKey(jobId)).WaitAsync(cancellationToken).ConfigureAwait(false);
        return entries.Length == 0 ? null : ParseJobState(entries);
    }

    public Task<bool> UpdateJobStatusAsync(string jobId, MessageExecutionStatus status, DateTimeOffset? startedUtc = null, DateTimeOffset? completedUtc = null, string? errorMessage = null, int? progress = null, int? attempt = null, TimeSpan? expiry = null, CancellationToken cancellationToken = default, string? workerId = null)
    {
        var updates = new List<HashEntry> { new("Status", FormatStatus(status)) };
        if (startedUtc.HasValue) updates.Add(new("StartedUtc", FormatTimestamp(startedUtc.Value)));
        if (IsTerminal(status)) updates.Add(new("CompletedUtc", FormatTimestamp(completedUtc ?? _timeProvider.GetUtcNow())));
        if (errorMessage is not null) updates.Add(new("ErrorMessage", errorMessage));
        if (progress.HasValue) updates.Add(new("Progress", FormatInt(progress.Value)));
        if (attempt.HasValue) updates.Add(new("Attempt", FormatInt(attempt.Value)));
        if (workerId is not null) updates.Add(new("WorkerId", workerId));
        if (status == MessageExecutionStatus.Processing)
        {
            updates.Add(new("Progress", FormatInt(progress ?? 0)));
            updates.Add(new("ProgressMessage", string.Empty));
            updates.Add(new("LastHeartbeatUtc", FormatTimestamp(startedUtc ?? _timeProvider.GetUtcNow())));
        }
        return MutateAsync("status", jobId, ResolveTtl(expiry, status), updates, cancellationToken);
    }

    public Task UpdateJobProgressAsync(string jobId, int progress, string? progressMessage = null, TimeSpan? expiry = null, CancellationToken cancellationToken = default, int? expectedAttempt = null)
        => MutateAsync("progress", jobId, ResolveTtl(expiry, MessageExecutionStatus.Processing),
            [new("Progress", FormatInt(progress)), new("ProgressMessage", progressMessage ?? string.Empty)], cancellationToken, expectedAttempt);

    public Task HeartbeatAsync(string jobId, CancellationToken cancellationToken = default, int? expectedAttempt = null, TimeSpan? expiry = null)
        => MutateAsync("heartbeat", jobId, expiry is null ? null : ResolveTtl(expiry, MessageExecutionStatus.Processing),
            [new("LastHeartbeatUtc", FormatTimestamp(_timeProvider.GetUtcNow()))], cancellationToken, expectedAttempt, preserveExpiry: expiry is null);

    public Task<bool> RequestCancellationAsync(string jobId, CancellationToken cancellationToken = default)
        => MutateAsync("cancel", jobId, null, [], cancellationToken);

    public Task<bool> IsCancellationRequestedAsync(string jobId, CancellationToken cancellationToken = default)
    {
        // The flag has the same expiration as its job and is created atomically with the status check.
        return _redis.GetDatabase().KeyExistsAsync(CancelKey(jobId)).WaitAsync(cancellationToken);
    }

    public Task RemoveJobStateAsync(string jobId, CancellationToken cancellationToken = default)
        => MutateAsync("remove", jobId, null, [], cancellationToken);

    private async Task<bool> MutateAsync(string operation, string jobId, TimeSpan? expiry, IReadOnlyList<HashEntry> fields,
        CancellationToken cancellationToken, int? expectedAttempt = null, bool preserveExpiry = false)
        => await EvaluateAsync(operation, jobId, expiry, fields, cancellationToken, expectedAttempt, preserveExpiry).ConfigureAwait(false) != 0;

    private async Task<long> EvaluateAsync(string operation, string jobId, TimeSpan? expiry, IReadOnlyList<HashEntry> fields,
        CancellationToken cancellationToken, int? expectedAttempt = null, bool preserveExpiry = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RedisValue[] args = new RedisValue[6 + fields.Count * 2];
        args[0] = operation;
        args[1] = _keyPrefix;
        args[2] = jobId;
        args[3] = preserveExpiry ? -2L : expiry is { } ttl ? Math.Max(1L, (long)ttl.TotalMilliseconds) : -1L;
        args[4] = expectedAttempt.HasValue ? FormatInt(expectedAttempt.Value) : string.Empty;
        args[5] = FormatTimestamp(_timeProvider.GetUtcNow());
        for (int i = 0; i < fields.Count; i++)
        {
            args[6 + i * 2] = fields[i].Name;
            args[7 + i * 2] = fields[i].Value;
        }
        return (long)await _redis.GetDatabase().ScriptEvaluateAsync(RedisMessageExecutionScripts.Mutate, [JobKey(jobId)], args)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CleanupAsync(string queueName, CancellationToken cancellationToken)
    {
        while (await EvaluateAsync("clean", queueName, null, [], cancellationToken).ConfigureAwait(false) == 128)
            cancellationToken.ThrowIfCancellationRequested();
    }

    public Task IncrementCounterAsync(string queueName, string counterName, long value = 1, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        var bucketKey = JobCounterBucketKey(queueName, _timeProvider.GetUtcNow());

        // Increment and retention refresh form one atomic server operation, without MULTI/EXEC overhead.
        return db.ScriptEvaluateAsync(RedisMessageExecutionScripts.IncrementCounter, [bucketKey],
            [counterName, value, (long)JobCounterBucketRetention.TotalMilliseconds]).WaitAsync(cancellationToken);
    }

    public async Task<JobCounterStats> GetCounterStatsAsync(string queueName, TimeSpan? window = null, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        var now = _timeProvider.GetUtcNow();
        var effectiveWindow = window ?? TimeSpan.FromHours(24);
        var startHour = TruncateToHour(now - effectiveWindow);
        var endHour = TruncateToHour(now);

        var hours = new List<DateTimeOffset>();
        for (var hour = startHour; hour <= endHour; hour = hour.AddHours(1))
            hours.Add(hour);

        var batch = db.CreateBatch();
        var tasks = new Task<HashEntry[]>[hours.Count];
        for (int i = 0; i < hours.Count; i++)
            tasks[i] = batch.HashGetAllAsync(JobCounterBucketKey(queueName, hours[i]));
        batch.Execute();

        var totals = new Dictionary<string, long>();
        var buckets = new List<JobCounterBucket>(hours.Count);

        for (int i = 0; i < hours.Count; i++)
        {
            var entries = await tasks[i].WaitAsync(cancellationToken).ConfigureAwait(false);
            var counters = new Dictionary<string, long>(entries.Length);

            foreach (var entry in entries)
            {
                if (entry.Value.TryParse(out long val))
                {
                    var name = entry.Name.ToString();
                    counters[name] = val;
                    totals[name] = totals.GetValueOrDefault(name) + val;
                }
            }

            buckets.Add(new JobCounterBucket { Hour = hours[i], Counters = counters });
        }

        return new JobCounterStats { Totals = totals, Buckets = buckets };
    }

    public async Task<IReadOnlyList<MessageExecutionState>> GetJobsByStatusAsync(string queueName, MessageExecutionStatus status, int skip = 0, int take = 50, CancellationToken cancellationToken = default)
    {
        if (take <= 0)
            return [];

        var db = _redis.GetDatabase();
        await CleanupAsync(queueName, cancellationToken).ConfigureAwait(false);
        var setKey = StatusSetKey(queueName, status);
        var results = new List<MessageExecutionState>(take);
        var dangling = new List<RedisValue>();
        long cursor = Math.Max(skip, 0);

        // Keep paging past members whose hash has expired so the caller still gets a full page.
        while (results.Count < take)
        {
            int wanted = take - results.Count;
            var members = await db.SortedSetRangeByRankAsync(setKey, cursor, cursor + wanted - 1, Order.Descending).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (members.Length == 0)
                break;

            cursor += members.Length;

            var batch = db.CreateBatch();
            var tasks = new Task<HashEntry[]>[members.Length];
            for (int i = 0; i < members.Length; i++)
                tasks[i] = batch.HashGetAllAsync(JobKey(members[i].ToString()));
            batch.Execute();

            for (int i = 0; i < tasks.Length; i++)
            {
                var entries = await tasks[i].WaitAsync(cancellationToken).ConfigureAwait(false);
                if (entries.Length > 0)
                {
                    var state = ParseJobState(entries);
                    if (state.Status == status && state.QueueName == queueName)
                        results.Add(state);
                }
                else
                    dangling.Add(members[i]);
            }

            if (members.Length < wanted)
                break;
        }

        // Removal is deferred until after paging so ranks stay stable while reading.
        foreach (var id in dangling)
            await MutateAsync("prune", id.ToString(), null, [new(queueName, string.Empty)], cancellationToken).ConfigureAwait(false);

        return results;
    }

    /// <summary>Counts current status-index entries after removing expired jobs using server deadlines.</summary>
    public async Task<long> GetJobCountByStatusAsync(string queueName, MessageExecutionStatus status, CancellationToken cancellationToken = default)
    {
        await CleanupAsync(queueName, cancellationToken).ConfigureAwait(false);
        return await _redis.GetDatabase().SortedSetLengthAsync(StatusSetKey(queueName, status)).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private TimeSpan? ResolveTtl(TimeSpan? expiry, MessageExecutionStatus status)
    {
        var ttl = expiry ?? _options.DefaultExpiry;
        if (ttl is null || IsTerminal(status))
            return ttl;

        return ttl.Value > _options.NonTerminalExpiry ? ttl : _options.NonTerminalExpiry;
    }

    private static bool IsTerminal(MessageExecutionStatus status)
        => status is MessageExecutionStatus.Completed or MessageExecutionStatus.Failed or MessageExecutionStatus.Cancelled;

    private static HashEntry[] BuildEntries(MessageExecutionState state, long createdScore)
    {
        var entries = new List<HashEntry>(12 + (state.Metadata?.Count ?? 0))
        {
            new("JobId", state.JobId),
            new("QueueName", state.QueueName),
            new("MessageType", state.MessageType),
            new("Status", FormatStatus(state.Status)),
            new("Progress", FormatInt(state.Progress)),
            new("ProgressMessage", state.ProgressMessage ?? string.Empty),
            new("CreatedUtc", createdScore.ToString(CultureInfo.InvariantCulture)),
            new("StartedUtc", state.StartedUtc is { } started ? FormatTimestamp(started) : string.Empty),
            new("CompletedUtc", state.CompletedUtc is { } completed ? FormatTimestamp(completed) : string.Empty),
            new("ErrorMessage", state.ErrorMessage ?? string.Empty),
            new("Attempt", FormatInt(state.Attempt)),
            new("WorkerId", state.WorkerId ?? string.Empty),
            new("LastUpdatedUtc", FormatTimestamp(state.LastUpdatedUtc)),
            new("LastHeartbeatUtc", state.LastHeartbeatUtc is { } heartbeat ? FormatTimestamp(heartbeat) : string.Empty)
        };

        if (state.Metadata is not null)
        {
            foreach (var kvp in state.Metadata)
                entries.Add(new(MetadataFieldPrefix + kvp.Key, kvp.Value));
        }

        return entries.ToArray();
    }

    private static string FormatStatus(MessageExecutionStatus status) => ((int)status).ToString(CultureInfo.InvariantCulture);
    private static string FormatInt(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string FormatTimestamp(DateTimeOffset value) => value.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

    private string JobKey(string jobId) => $"{_keyPrefix}:{jobId}";
    private string CancelKey(string jobId) => $"{_keyPrefix}:{jobId}:cancel";
    private string QueueSetKey(string queueName) => $"{_keyPrefix}:queues:{queueName}";
    private string StatusSetKey(string queueName, MessageExecutionStatus status) => $"{_keyPrefix}:queues:{queueName}:status:{(int)status}";
    private string JobCounterBucketKey(string queueName, DateTimeOffset timestamp) => $"{_keyPrefix}:counters:{queueName}:{TruncateToHour(timestamp):yyyy-MM-ddTHH}";

    private static DateTimeOffset TruncateToHour(DateTimeOffset timestamp)
        => new(timestamp.Year, timestamp.Month, timestamp.Day, timestamp.Hour, 0, 0, TimeSpan.Zero);

    private static MessageExecutionState ParseJobState(HashEntry[] entries)
    {
        var dict = new Dictionary<string, string>(entries.Length);
        Dictionary<string, string>? metadata = null;

        foreach (var entry in entries)
        {
            var name = entry.Name.ToString();
            if (name.StartsWith(MetadataFieldPrefix, StringComparison.Ordinal))
                (metadata ??= new Dictionary<string, string>())[name[MetadataFieldPrefix.Length..]] = entry.Value.ToString();
            else
                dict[name] = entry.Value.ToString();
        }

        return new MessageExecutionState
        {
            JobId = dict.GetValueOrDefault("JobId") ?? string.Empty,
            QueueName = dict.GetValueOrDefault("QueueName") ?? string.Empty,
            MessageType = dict.GetValueOrDefault("MessageType") ?? string.Empty,
            Status = int.TryParse(dict.GetValueOrDefault("Status"), out var s) ? (MessageExecutionStatus)s : MessageExecutionStatus.Queued,
            Progress = int.TryParse(dict.GetValueOrDefault("Progress"), out var p) ? p : 0,
            ProgressMessage = NullIfEmpty(dict.GetValueOrDefault("ProgressMessage")),
            CreatedUtc = ParseDateTimeOffset(dict.GetValueOrDefault("CreatedUtc")),
            StartedUtc = ParseNullableDateTimeOffset(dict.GetValueOrDefault("StartedUtc")),
            CompletedUtc = ParseNullableDateTimeOffset(dict.GetValueOrDefault("CompletedUtc")),
            ErrorMessage = NullIfEmpty(dict.GetValueOrDefault("ErrorMessage")),
            Attempt = int.TryParse(dict.GetValueOrDefault("Attempt"), out var a) ? a : 0,
            WorkerId = string.IsNullOrEmpty(dict.GetValueOrDefault("WorkerId")) ? null : dict["WorkerId"],
            LastUpdatedUtc = ParseDateTimeOffset(dict.GetValueOrDefault("LastUpdatedUtc")),
            LastHeartbeatUtc = ParseNullableDateTimeOffset(dict.GetValueOrDefault("LastHeartbeatUtc")),
            Metadata = metadata
        };
    }

    private static DateTimeOffset ParseDateTimeOffset(string? value)
        => long.TryParse(value, out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : DateTimeOffset.MinValue;

    private static DateTimeOffset? ParseNullableDateTimeOffset(string? value)
        => string.IsNullOrEmpty(value) ? null : long.TryParse(value, out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrEmpty(value) ? null : value;
}

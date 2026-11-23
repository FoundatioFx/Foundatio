using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Messaging.Transport;
using Foundatio.Redis.Utility;
using StackExchange.Redis;

namespace Foundatio.Jobs.Store;

/// <summary>
/// A job store on Redis. Each job is a hash; per-name sorted sets of queued jobs (by availability) and running jobs
/// (by lease expiry) make claims a few log-time lookups, and lexicographic indexes ordered by creation time serve
/// queries and counts. Every operation is a Lua script that reads "now" from the Redis clock and touches only the
/// keys passed to it, so the store also runs on Redis-compatible servers that enforce declared keys. Per-name hashes
/// hold the concurrency keys of running jobs and the deduplication keys of jobs not yet claimed, and a per-name set
/// holds the due jobs waiting for a concurrency key, so claims don't pass over them again. Claims and
/// queries start deleting jobs whose retention has run out, in batches about once a second, without waiting for it;
/// counts of finished jobs wait for that, or delete them first, whenever one has expired.
/// </summary>
public sealed class RedisJobStore : IJobStore
{
    private const int ClaimCandidates = 8;
    private const int MaxClaimRounds = 10;
    private const int MaxClaimBatch = 100;
    // A claim token is this many random characters and the claim's number in 8 hex digits (TokenNameOffset in all), then the job's name.
    private const int ClaimTokenPrefixLength = 24;
    private const int MaxRetries = 3;
    private const int SweepBatchSize = 250;
    private const int MaxSweepBatches = 20;
    private const int TokenNameOffset = 32;
    private const string HeaderFieldPrefix = "header:";
    private const int MaxCachedNames = 1024;
    private static readonly long SweepIntervalMilliseconds = (long)TimeSpan.FromSeconds(1).TotalMilliseconds;
    private static readonly TimeSpan SweepTimeBudget = TimeSpan.FromMilliseconds(500);

    private static readonly string Common =
        $"local QUEUED, RUNNING, SUCCEEDED, FAILED, CANCELLED = '{Status(JobState.Queued)}', '{Status(JobState.Running)}', '{Status(JobState.Succeeded)}', '{Status(JobState.Failed)}', '{Status(JobState.Cancelled)}'\n"
        + EmbeddedResourceLoader.GetEmbeddedResource("Foundatio.Redis.Scripts.JobStore.Common.lua");

    // Declared before the scripts that include it: static fields are initialized in order.
    private static readonly string KeysCommon = EmbeddedResourceLoader.GetEmbeddedResource("Foundatio.Redis.Scripts.JobStore.Keys.lua");

    private static readonly RedisScript CreateScript = LoadScript("Create");
    private static readonly RedisScript GetScript = LoadScript("Get");
    private static readonly RedisScript QueryScript = LoadScript("Query");
    private static readonly RedisScript CandidatesScript = LoadScript("Candidates");
    private static readonly RedisScript ClaimScript = LoadScript("Claim", withKeys: true);
    private static readonly RedisScript RenewScript = LoadScript("Renew");
    private static readonly RedisScript ProgressScript = LoadScript("Progress");
    private static readonly RedisScript CompleteScript = LoadScript("Complete", withKeys: true);
    private static readonly RedisScript CancelScript = LoadScript("Cancel", withKeys: true);
    private static readonly RedisScript RemoveScript = LoadScript("Remove", withKeys: true);
    private static readonly RedisScript ExpiredScript = LoadScript("Expired");
    private static readonly RedisScript NamesScript = LoadScript("Names");
    private static readonly RedisScript SweepScript = LoadScript("Sweep");

    private readonly IConnectionMultiplexer _connection;
    private readonly RedisJobStoreOptions _options;
    private readonly string _prefix;
    private readonly RedisKey _indexKey;
    private readonly RedisKey[] _statusIndexKeys;
    private readonly ConcurrentDictionary<string, NameKeys> _nameKeys = new(StringComparer.Ordinal);
    private readonly object _sweepLock = new();
    private Task _sweep = Task.CompletedTask;
    private long _nextSweep;

    /// <summary>Creates a store on <paramref name="connection"/>, which the caller owns and disposes.</summary>
    /// <exception cref="ArgumentException"><see cref="RedisJobStoreOptions.Prefix"/> is empty or contains '{' or '}'.</exception>
    public RedisJobStore(IConnectionMultiplexer connection, RedisJobStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _options = options ?? new RedisJobStoreOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.Prefix, nameof(options));
        if (_options.Prefix.AsSpan().IndexOfAny('{', '}') >= 0)
            throw new ArgumentException("The key prefix cannot contain '{' or '}'; it is used as the store's hash tag.", nameof(options));

        _connection = connection;
        _prefix = $"{{{_options.Prefix}}}:";
        _indexKey = _prefix + "index";
        ExpiringKey = _prefix + "expiring";
        _statusIndexKeys = Enum.GetValues<JobState>().Select(state => (RedisKey)$"{_prefix}index:status:{Status(state)}").ToArray();
    }

    private IDatabase Database => _connection.GetDatabase(_options.Database);

    /// <inheritdoc />
    Task<string?> IJobStore.CreateAsync(JobInfo job, CancellationToken cancellationToken) => CreateAsync(job, null, cancellationToken);

    /// <inheritdoc />
    Task<string?> IJobStore.CreateAsync(JobInfo job, TimeSpan availableAfter, CancellationToken cancellationToken) => CreateAsync(job, (TimeSpan?)availableAfter, cancellationToken);

    /// <param name="job">The job to add.</param>
    /// <param name="availableAfter">When set, the job becomes available this long after now on the Redis clock, instead of at <see cref="JobInfo.AvailableAt"/>.</param>
    /// <param name="cancellationToken">Checked before the job is sent.</param>
    private async Task<string?> CreateAsync(JobInfo job, TimeSpan? availableAfter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentException.ThrowIfNullOrWhiteSpace(job.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(job.Name);
        cancellationToken.ThrowIfCancellationRequested();

        string member = $"{job.CreatedAt.UtcTicks:x16}|{job.Id}";
        string deduplicationKey = job.DeduplicationKey ?? String.Empty;
        var values = availableAfter is { } after
            ? new List<RedisValue> { job.Id, job.Name, ToMicroseconds(after), "1", member, deduplicationKey }
            : [job.Id, job.Name, ToMicroseconds(job.AvailableAt), "0", member, deduplicationKey];
        values.AddRange(["payload", job.Payload, "created", ToMicroseconds(job.CreatedAt)]);
        AddIfPresent(values, "ckey", job.ConcurrencyKey);
        AddIfPresent(values, "dkey", job.DeduplicationKey);
        if (job.ScheduledFor != default || availableAfter is null)
            values.AddRange(["scheduled", ToMicroseconds(job.ScheduledFor == default ? job.AvailableAt : job.ScheduledFor)]);
        AddIfPresent(values, "correlation", job.CorrelationId);
        AddIfPresent(values, "schedule", job.ScheduleName);
        AddIfPresent(values, "started", job.StartedAt);
        AddIfPresent(values, "completed", job.CompletedAt);
        AddIfPresent(values, "retain", job.RetainUntil);
        AddIfPresent(values, "progress", job.Progress?.ToString(CultureInfo.InvariantCulture));
        AddIfPresent(values, "message", job.Message);
        AddIfPresent(values, "error", job.Error);
        if (job.CancellationRequested)
            values.AddRange(["cancel", "1"]);
        foreach (var (header, value) in job.Headers)
            values.AddRange([HeaderFieldPrefix + header, value]);

        RedisKey[] keys =
        [
            JobKey(job.Id), QueuedKey(job.Name), IndexKey(null, null), IndexKey(null, job.Name),
            IndexKey(JobState.Queued, null), IndexKey(JobState.Queued, job.Name), PendingKey(job.Name)
        ];

        try
        {
            for (int attempt = 1; ; attempt++)
            {
                var result = await EvaluateScriptAsync(CreateScript, keys, values.ToArray()).AnyContext();
                if (result.Resp2Type == ResultType.BulkString)
                    return (string?)result;

                long created = (long)result;
                if (created != -1 || attempt == MaxRetries)
                    return created == 1 ? job.Id : null;

                // A finished job whose retention ran out still holds the id; remove it, then create the new one.
                await RemoveAsync(job.Id, onlyIfExpired: true).AnyContext();
            }
        }
        catch (Exception ex) when (RedisMessageTransport.IsRedisFailure(ex))
        {
            throw Failure($"create job {job.Id}", ex);
        }
    }

    /// <inheritdoc />
    async Task<JobInfo?> IJobStore.GetAsync(string jobId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await ReadJobAsync(jobId).AnyContext();
        }
        catch (Exception ex) when (RedisMessageTransport.IsRedisFailure(ex))
        {
            throw Failure($"read job {jobId}", ex);
        }
    }

    /// <inheritdoc />
    async Task<JobPage<JobInfo>> IJobStore.QueryAsync(JobQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Limit, 1, nameof(query));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.Limit, 1000, nameof(query));
        cancellationToken.ThrowIfCancellationRequested();

        string continuation = query.ContinuationToken ?? String.Empty;
        if (continuation.Length > 0 && (continuation.Length < 18 || continuation[16] != '|' || !Int64.TryParse(continuation.AsSpan(0, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _)))
            throw new ArgumentException("The continuation token is not valid.", nameof(query));

        try
        {
            SweepIfDue();
            var members = (string[])(await EvaluateScriptAsync(QueryScript, [IndexKey(query.State, query.Name)], [continuation, query.Limit]).AnyContext())!;
            var page = members.Take(query.Limit).ToArray();
            var jobs = await Task.WhenAll(page.Select(member => ReadJobAsync(member[(member.IndexOf('|') + 1)..]))).AnyContext();
            return new JobPage<JobInfo>(jobs.Where(j => j is not null).Select(j => j!).ToList(), members.Length > query.Limit ? page[^1] : null);
        }
        catch (Exception ex) when (RedisMessageTransport.IsRedisFailure(ex))
        {
            throw Failure("query jobs", ex);
        }
    }

    /// <inheritdoc />
    async Task<long> IJobStore.CountAsync(JobState? state, string? name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            // Only finished jobs expire, so queued and running counts never include one.
            if (state is not (JobState.Queued or JobState.Running))
                await SweepExpiredAsync(cancellationToken).AnyContext();

            return await Database.SortedSetLengthAsync(IndexKey(state, name)).AnyContext();
        }
        catch (Exception ex) when (RedisMessageTransport.IsRedisFailure(ex))
        {
            throw Failure("count jobs", ex);
        }
    }

    /// <inheritdoc />
    async Task<JobInfo?> IJobStore.ClaimAsync(JobClaimRequest request, CancellationToken cancellationToken)
    {
        var claimed = await ((IJobStore)this).ClaimBatchAsync(request, 1, cancellationToken).AnyContext();
        return claimed.Count > 0 ? claimed[0] : null;
    }

    /// <inheritdoc />
    async Task<IReadOnlyList<JobInfo>> IJobStore.ClaimBatchAsync(JobClaimRequest request, int maxCount, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Names);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkerId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.LeaseDuration, TimeSpan.Zero, nameof(request));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Names.Count == 0)
            return [];

        try
        {
            SweepIfDue();
            string[] names = request.Names.Distinct(StringComparer.Ordinal).ToArray();
            var setKeys = names.SelectMany(n => new RedisKey[] { QueuedKey(n), RunningKey(n) }).ToArray();
            int claimCount = Math.Min(maxCount, MaxClaimBatch);
            int candidateCount = ClaimCandidates + claimCount;
            var maxPerName = names.Select(n => request.MaxPerName is { } limits && limits.TryGetValue(n, out int max) ? Math.Clamp(max, 0, claimCount) : claimCount).ToArray();
            var candidateValues = new RedisValue[1 + names.Length];
            candidateValues[0] = candidateCount;
            for (int i = 0; i < names.Length; i++)
                candidateValues[1 + i] = Math.Min(candidateCount, ClaimCandidates + maxPerName[i]);

            // Find the jobs due longest, then claim those of them that no other worker has taken in the meantime.
            // Two scripts, because a script can only touch the jobs whose keys it is given.
            for (int round = 0; round < MaxClaimRounds; round++)
            {
                var candidates = (RedisResult[])(await EvaluateScriptAsync(CandidatesScript, setKeys, candidateValues).AnyContext())!;
                if (candidates.Length == 0)
                    return [];

                var keys = new List<RedisKey>(2 + 7 * names.Length + candidates.Length / 3) { IndexKey(JobState.Queued, null), IndexKey(JobState.Running, null) };
                foreach (string name in names)
                {
                    keys.AddRange([QueuedKey(name), RunningKey(name), IndexKey(JobState.Queued, name), IndexKey(JobState.Running, name),
                        HoldersKey(name), WaitingKey(name), PendingKey(name)]);
                }

                var values = new List<RedisValue>(5 + 2 * names.Length + candidates.Length)
                {
                    request.WorkerId, ToMicroseconds(request.LeaseDuration), Guid.NewGuid().ToString("N")[..ClaimTokenPrefixLength], names.Length, claimCount
                };
                values.AddRange(names.Select(n => (RedisValue)n));
                values.AddRange(maxPerName.Select(max => (RedisValue)max));
                for (int i = 0; i + 2 < candidates.Length; i += 3)
                {
                    string id = (string)candidates[i]!;
                    keys.Add(JobKey(id));
                    values.AddRange([id, (long)candidates[i + 1], (string)candidates[i + 2]!]);
                }

                var claimed = (RedisResult[])(await EvaluateScriptAsync(ClaimScript, keys.ToArray(), values.ToArray()).AnyContext())!;
                if (claimed.Length > 0)
                    return claimed.Select(job => ToJob((RedisValue[])job!)).ToList();
            }

            return [];
        }
        catch (Exception ex) when (RedisMessageTransport.IsRedisFailure(ex))
        {
            throw Failure("claim jobs", ex);
        }
    }

    /// <inheritdoc />
    async Task<JobLeaseStatus> IJobStore.RenewAsync(string jobId, string claimToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (await GetClaimedNameAsync(jobId, claimToken).AnyContext() is not { } name)
                return JobLeaseStatus.Lost;

            return await EvaluateAsync(RenewScript, [JobKey(jobId), RunningKey(name)], [jobId, claimToken, ToMicroseconds(leaseDuration), name]).AnyContext() switch
            {
                1 => JobLeaseStatus.Renewed,
                2 => JobLeaseStatus.CancellationRequested,
                _ => JobLeaseStatus.Lost
            };
        }
        catch (Exception ex) when (RedisMessageTransport.IsRedisFailure(ex))
        {
            throw Failure($"renew job {jobId}", ex);
        }
    }

    /// <inheritdoc />
    async Task<bool> IJobStore.ReportProgressAsync(string jobId, string claimToken, int? percent, string? message, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return await EvaluateAsync(ProgressScript, [JobKey(jobId)],
                [claimToken, percent.HasValue ? 1 : 0, percent ?? 0, message is not null ? 1 : 0, message ?? String.Empty]).AnyContext() == 1;
        }
        catch (Exception ex) when (RedisMessageTransport.IsRedisFailure(ex))
        {
            throw Failure($"report progress of job {jobId}", ex);
        }
    }

    /// <inheritdoc />
    async Task<bool> IJobStore.CompleteAsync(string jobId, string claimToken, JobCompletion completion, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);
        ArgumentNullException.ThrowIfNull(completion);
        cancellationToken.ThrowIfCancellationRequested();
        if (completion.State == JobState.Running)
            throw new ArgumentException("A run cannot complete as Running.", nameof(completion));

        try
        {
            if (await GetClaimedNameAsync(jobId, claimToken).AnyContext() is not { } name)
                return false;

            RedisKey[] keys =
            [
                JobKey(jobId), RunningKey(name), QueuedKey(name), ExpiringKey, IndexKey(JobState.Running, null), IndexKey(JobState.Running, name),
                IndexKey(completion.State, null), IndexKey(completion.State, name), HoldersKey(name), WaitingKey(name)
            ];

            return await EvaluateAsync(CompleteScript, keys, [
                jobId, claimToken, Status(completion.State), ToMicroseconds(completion.RetryDelay), completion.RefundAttempt ? 1 : 0,
                ToMicroseconds(completion.RetainFor), completion.Message is not null ? 1 : 0, completion.Message ?? String.Empty,
                completion.Error is not null ? 1 : 0, completion.Error ?? String.Empty, name
            ]).AnyContext() == 1;
        }
        catch (Exception ex) when (RedisMessageTransport.IsRedisFailure(ex))
        {
            throw Failure($"complete job {jobId}", ex);
        }
    }

    /// <inheritdoc />
    async Task<bool> IJobStore.RequestCancellationAsync(string jobId, TimeSpan retainFor, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            for (int attempt = 1; ; attempt++)
            {
                string? name = await Database.HashGetAsync(JobKey(jobId), "name").AnyContext();
                if (name is null)
                    return false;

                RedisKey[] keys =
                [
                    JobKey(jobId), QueuedKey(name), ExpiringKey, IndexKey(JobState.Queued, null), IndexKey(JobState.Queued, name),
                    IndexKey(JobState.Cancelled, null), IndexKey(JobState.Cancelled, name), HoldersKey(name), WaitingKey(name), PendingKey(name)
                ];

                long cancelled = await EvaluateAsync(CancelScript, keys, [jobId, ToMicroseconds(retainFor), name]).AnyContext();
                if (cancelled != -1 || attempt == MaxRetries)
                    return cancelled == 1;
            }
        }
        catch (Exception ex) when (RedisMessageTransport.IsRedisFailure(ex))
        {
            throw Failure($"cancel job {jobId}", ex);
        }
    }

    /// <inheritdoc />
    async Task<bool> IJobStore.DeleteAsync(string jobId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await RemoveAsync(jobId, onlyIfExpired: false).AnyContext() == 1;
        }
        catch (Exception ex) when (RedisMessageTransport.IsRedisFailure(ex))
        {
            throw Failure($"delete job {jobId}", ex);
        }
    }

    /// <summary>
    /// Removes a job and its index entries: any job, or only one whose retention has run out. The job's name and
    /// status say which keys hold it, so they are read first and checked again by the script.
    /// </summary>
    /// <returns>1 when a live job was removed, 2 when an expired one was, 0 when nothing was removed, -1 when the job kept changing.</returns>
    private async Task<long> RemoveAsync(string jobId, bool onlyIfExpired)
    {
        for (int attempt = 1; ; attempt++)
        {
            var fields = await Database.HashGetAsync(JobKey(jobId), ["name", "status", "index"]).AnyContext();
            RedisKey[] keys = fields[0].IsNull
                ? [JobKey(jobId), ExpiringKey]
                :
                [
                    JobKey(jobId), ExpiringKey, QueuedKey(fields[0]!), RunningKey(fields[0]!), IndexKey(null, null), IndexKey(null, fields[0]),
                    StatusIndexKey(fields[1]!, null), StatusIndexKey(fields[1]!, fields[0]), HoldersKey(fields[0]!), WaitingKey(fields[0]!), PendingKey(fields[0]!)
                ];

            long removed = await EvaluateAsync(RemoveScript, keys,
                [jobId, fields[0].IsNull ? String.Empty : fields[0], fields[1].IsNull ? String.Empty : fields[1], fields[2].IsNull ? String.Empty : fields[2], onlyIfExpired ? 1 : 0]).AnyContext();
            if (removed != -1 || attempt == MaxRetries)
                return removed;
        }
    }

    /// <summary>
    /// Starts removing finished jobs whose retention has run out, at most once a second, for calls that only need it
    /// done eventually, and returns without waiting for it. If it fails, the next sweep tries again; counts, which wait
    /// for theirs, report the failure.
    /// </summary>
    private void SweepIfDue()
    {
        lock (_sweepLock)
        {
            if (_sweep.IsCompleted && Environment.TickCount64 >= _nextSweep)
                _ = StartSweep().ContinueWith(static sweep => _ = sweep.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    /// <summary>Waits for the sweep that is running, or starts one when some job's retention has run out.</summary>
    private async Task SweepExpiredAsync(CancellationToken cancellationToken)
    {
        Task? sweep;
        lock (_sweepLock)
            sweep = _sweep.IsCompleted ? null : _sweep;

        if (sweep is null)
        {
            if ((await GetExpiredAsync(1).AnyContext()).Length == 0)
                return;

            lock (_sweepLock)
                sweep = _sweep.IsCompleted ? StartSweep() : _sweep;
        }

        await sweep.WaitAsync(cancellationToken).AnyContext();
    }

    /// <summary>Starts a sweep that callers share. Call it holding <see cref="_sweepLock"/>.</summary>
    private Task StartSweep()
    {
        _nextSweep = Environment.TickCount64 + SweepIntervalMilliseconds;
        return _sweep = SweepAsync();
    }

    /// <summary>
    /// Removes expired jobs a batch at a time until a batch comes back short, so the sweep keeps up with however many
    /// jobs expire, within <see cref="MaxSweepBatches"/> and <see cref="SweepTimeBudget"/>.
    /// </summary>
    private async Task SweepAsync()
    {
        long started = Stopwatch.GetTimestamp();
        for (int batch = 0; batch < MaxSweepBatches && Stopwatch.GetElapsedTime(started) < SweepTimeBudget; batch++)
        {
            string[] expired = await GetExpiredAsync(SweepBatchSize).AnyContext();
            if (expired.Length > 0)
                await RemoveExpiredAsync(expired).AnyContext();

            if (expired.Length < SweepBatchSize)
                return;
        }
    }

    private async Task<string[]> GetExpiredAsync(int limit)
    {
        return (string[])(await EvaluateScriptAsync(ExpiredScript, [ExpiringKey], [limit]).AnyContext())!;
    }

    /// <summary>
    /// Removes a batch of expired jobs and their index entries in one script. Their names say which index keys hold
    /// them, so they are read first and checked again by the script.
    /// </summary>
    private async Task RemoveExpiredAsync(string[] ids)
    {
        var jobKeys = ids.Select(JobKey).ToArray();
        var names = (string[])(await EvaluateScriptAsync(NamesScript, jobKeys).AnyContext())!;
        var distinct = new List<string>();
        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string name in names)
        {
            if (name.Length > 0 && ordinals.TryAdd(name, distinct.Count + 1))
                distinct.Add(name);
        }

        var keys = new List<RedisKey>(5 + 6 * distinct.Count + jobKeys.Length)
        {
            ExpiringKey, IndexKey(null, null), IndexKey(JobState.Succeeded, null), IndexKey(JobState.Failed, null), IndexKey(JobState.Cancelled, null)
        };
        var values = new List<RedisValue>(1 + distinct.Count + 2 * ids.Length) { distinct.Count };
        foreach (string name in distinct)
        {
            keys.AddRange([QueuedKey(name), RunningKey(name), IndexKey(null, name), IndexKey(JobState.Succeeded, name), IndexKey(JobState.Failed, name), IndexKey(JobState.Cancelled, name)]);
            values.Add(name);
        }

        keys.AddRange(jobKeys);
        for (int i = 0; i < ids.Length; i++)
            values.AddRange([ids[i], names[i].Length > 0 ? ordinals[names[i]] : 0]);

        await EvaluateScriptAsync(SweepScript, keys.ToArray(), values.ToArray()).AnyContext();
    }

    private async Task<JobInfo?> ReadJobAsync(string jobId)
    {
        var result = await EvaluateScriptAsync(GetScript, [JobKey(jobId)]).AnyContext();
        return result.IsNull ? null : ToJob((RedisValue[])result!);
    }

    /// <summary>
    /// The name of the job a claim token was issued for. Claim tokens carry it, so renewals and completions can
    /// declare the job's keys without reading it first; any other token is looked up.
    /// </summary>
    private async Task<string?> GetClaimedNameAsync(string jobId, string claimToken)
    {
        if (claimToken.Length > TokenNameOffset)
            return claimToken[TokenNameOffset..];

        return await Database.HashGetAsync(JobKey(jobId), "name").AnyContext();
    }

    private static JobStoreException Failure(string operation, Exception exception) => new($"Could not {operation}: {exception.Message}", exception);

    private async Task<long> EvaluateAsync(RedisScript script, RedisKey[] keys, RedisValue[] values)
    {
        return (long)await EvaluateScriptAsync(script, keys, values).AnyContext();
    }

    /// <summary>Runs <paramref name="script"/> by its hash, loading it when the server doesn't have it (after a restart or a failover).</summary>
    private async Task<RedisResult> EvaluateScriptAsync(RedisScript script, RedisKey[] keys, RedisValue[]? values = null)
    {
        try
        {
            return await Database.ScriptEvaluateAsync(script.Hash, keys, values).AnyContext();
        }
        catch (RedisServerException ex) when (ex.Message.StartsWith("NOSCRIPT", StringComparison.Ordinal))
        {
            return await Database.ScriptEvaluateAsync(script.Text, keys, values).AnyContext();
        }
    }

    private RedisKey JobKey(string jobId) => _prefix + "job:" + jobId;

    private RedisKey QueuedKey(string name) => GetNameKeys(name).Queued;

    private RedisKey RunningKey(string name) => GetNameKeys(name).Running;

    private RedisKey ExpiringKey { get; }

    /// <summary>The concurrency keys of <paramref name="name"/>'s running jobs, each with the id of the job holding it.</summary>
    private RedisKey HoldersKey(string name) => GetNameKeys(name).Holders;

    /// <summary>The due jobs of <paramref name="name"/> waiting for a concurrency key another job holds.</summary>
    private RedisKey WaitingKey(string name) => GetNameKeys(name).Waiting;

    /// <summary>The deduplication keys of <paramref name="name"/>'s jobs that have not been claimed, each with the job's id.</summary>
    private RedisKey PendingKey(string name) => GetNameKeys(name).Pending;

    private RedisKey IndexKey(JobState? status, string? name)
    {
        if (name is null)
            return status is { } value ? _statusIndexKeys[(int)value] : _indexKey;

        var keys = GetNameKeys(name);
        return status is { } state ? keys.StatusIndexes[(int)state] : keys.Index;
    }

    private RedisKey StatusIndexKey(string status, string? name) => IndexKey((JobState)Int32.Parse(status, NumberStyles.None, CultureInfo.InvariantCulture), name);

    /// <summary>The keys of one job name, built once per name rather than for every call.</summary>
    private NameKeys GetNameKeys(string name)
    {
        if (_nameKeys.TryGetValue(name, out var keys))
            return keys;

        keys = new NameKeys(_prefix, name);
        return _nameKeys.Count < MaxCachedNames ? _nameKeys.GetOrAdd(name, keys) : keys;
    }

    private sealed class NameKeys(string prefix, string name)
    {
        public RedisKey Queued { get; } = prefix + "queued:" + name;
        public RedisKey Running { get; } = prefix + "running:" + name;
        public RedisKey Holders { get; } = prefix + "holders:" + name;
        public RedisKey Waiting { get; } = prefix + "waiting:" + name;
        public RedisKey Pending { get; } = prefix + "pending:" + name;
        public RedisKey Index { get; } = $"{prefix}index:name:{name}";
        public RedisKey[] StatusIndexes { get; } = Enum.GetValues<JobState>().Select(state => (RedisKey)$"{prefix}index:status:{Status(state)}:{name}").ToArray();
    }

    private static void AddIfPresent(List<RedisValue> values, string field, string? value)
    {
        if (value is not null)
            values.AddRange([field, value]);
    }

    private static void AddIfPresent(List<RedisValue> values, string field, DateTimeOffset? value)
    {
        if (value is { } time)
            values.AddRange([field, ToMicroseconds(time)]);
    }

    private static JobInfo ToJob(RedisValue[] pairs)
    {
        var fields = new Dictionary<string, RedisValue>(pairs.Length / 2, StringComparer.Ordinal);
        Dictionary<string, string>? headers = null;
        for (int i = 0; i + 1 < pairs.Length; i += 2)
        {
            string field = pairs[i]!;
            if (field.StartsWith(HeaderFieldPrefix, StringComparison.Ordinal))
                (headers ??= new Dictionary<string, string>(StringComparer.Ordinal))[field[HeaderFieldPrefix.Length..]] = (string?)pairs[i + 1] ?? String.Empty;
            else
                fields[field] = pairs[i + 1];
        }

        string? Text(string field) => fields.TryGetValue(field, out var value) ? (string?)value : null;
        DateTimeOffset? Time(string field) => fields.TryGetValue(field, out var value) ? FromMicroseconds((long)value) : null;

        return new JobInfo
        {
            Id = Text("id")!,
            Name = Text("name")!,
            State = (JobState)(int)fields["status"],
            Payload = fields.TryGetValue("payload", out var payload) ? (byte[]?)payload ?? [] : [],
            CorrelationId = Text("correlation"),
            ScheduleName = Text("schedule"),
            ConcurrencyKey = NullIfEmpty(Text("ckey")),
            DeduplicationKey = NullIfEmpty(Text("dkey")),
            Headers = headers ?? (IReadOnlyDictionary<string, string>)ReadOnlyDictionary<string, string>.Empty,
            Attempt = (int)fields["attempt"],
            CreatedAt = Time("created") ?? default,
            AvailableAt = Time("available") ?? default,
            ScheduledFor = Time("scheduled") ?? Time("available") ?? default,
            RetainUntil = Time("retain"),
            UpdatedAt = Time("updated") ?? default,
            StartedAt = Time("started"),
            CompletedAt = Time("completed"),
            Progress = fields.TryGetValue("progress", out var progress) ? (int)progress : null,
            Message = Text("message"),
            Error = Text("error"),
            CancellationRequested = Text("cancel") == "1",
            WorkerId = Text("worker"),
            ClaimToken = Text("token"),
            LeaseExpiresAt = Time("lease")
        };
    }

    private static string? NullIfEmpty(string? value) => String.IsNullOrEmpty(value) ? null : value;

    private static long ToMicroseconds(DateTimeOffset time) => (time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / TimeSpan.TicksPerMicrosecond;

    private static long ToMicroseconds(TimeSpan duration) => duration <= TimeSpan.Zero ? 0 : duration.Ticks / TimeSpan.TicksPerMicrosecond;

    private static DateTimeOffset FromMicroseconds(long microseconds) => DateTimeOffset.UnixEpoch.AddTicks(microseconds * TimeSpan.TicksPerMicrosecond);

    private static string Status(JobState status) => ((int)status).ToString(CultureInfo.InvariantCulture);

    private static RedisScript LoadScript(string name, bool withKeys = false)
    {
        return new RedisScript(Common + (withKeys ? KeysCommon : String.Empty) + EmbeddedResourceLoader.GetEmbeddedResource($"Foundatio.Redis.Scripts.JobStore.{name}.lua"));
    }

    /// <summary>
    /// A script and its SHA1, so a call sends EVALSHA without the client hashing the script's text each time (which
    /// costs more the longer the script, on the connection's one writer), and sends the text only when Redis lacks it.
    /// </summary>
    private sealed class RedisScript
    {
        public RedisScript(string text)
        {
            Text = text;
            Hash = SHA1.HashData(Encoding.UTF8.GetBytes(text));
        }

        public string Text { get; }

        public byte[] Hash { get; }
    }
}

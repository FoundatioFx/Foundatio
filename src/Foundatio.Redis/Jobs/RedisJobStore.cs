using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Redis.Utility;
using StackExchange.Redis;

namespace Foundatio.Jobs.Store;

/// <summary>
/// A job store on Redis. Each job is a hash; per-name sorted sets of queued jobs (by availability) and running jobs
/// (by lease expiry) make claims a few log-time lookups, and lexicographic indexes ordered by creation time serve
/// queries and counts. Every operation is a Lua script that reads "now" from the Redis clock and touches only the
/// keys passed to it, so the store also runs on Redis-compatible servers that enforce declared keys.
/// </summary>
public sealed class RedisJobStore : IJobStore
{
    private const int ClaimCandidates = 8;
    private const int MaxClaimRounds = 10;
    private const int MaxRetries = 3;
    private const int SweepLimit = 100;
    private const int CountSweepLimit = 1000;
    private const int TokenNameOffset = 32;
    private static readonly long SweepIntervalMilliseconds = (long)TimeSpan.FromSeconds(1).TotalMilliseconds;

    private static readonly string Common =
        $"local QUEUED, RUNNING, SUCCEEDED, CANCELLED = '{Status(JobStatus.Queued)}', '{Status(JobStatus.Running)}', '{Status(JobStatus.Succeeded)}', '{Status(JobStatus.Cancelled)}'\n"
        + EmbeddedResourceLoader.GetEmbeddedResource("Foundatio.Redis.Scripts.JobStore.Common.lua");

    private static readonly string CreateScript = LoadScript("Create");
    private static readonly string GetScript = LoadScript("Get");
    private static readonly string QueryScript = LoadScript("Query");
    private static readonly string CandidatesScript = LoadScript("Candidates");
    private static readonly string ClaimScript = LoadScript("Claim");
    private static readonly string RenewScript = LoadScript("Renew");
    private static readonly string ProgressScript = LoadScript("Progress");
    private static readonly string CompleteScript = LoadScript("Complete");
    private static readonly string CancelScript = LoadScript("Cancel");
    private static readonly string RemoveScript = LoadScript("Remove");
    private static readonly string ExpiredScript = LoadScript("Expired");

    private readonly IConnectionMultiplexer _connection;
    private readonly RedisJobStoreOptions _options;
    private readonly string _prefix;
    private long _nextSweep;
    private int _sweeping;

    public RedisJobStore(IConnectionMultiplexer connection, RedisJobStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _options = options ?? new RedisJobStoreOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.KeyPrefix, nameof(options));
        if (_options.KeyPrefix.AsSpan().IndexOfAny('{', '}') >= 0)
            throw new ArgumentException("The key prefix cannot contain '{' or '}'; it is used as the store's hash tag.", nameof(options));

        _connection = connection;
        _prefix = $"{{{_options.KeyPrefix}}}:";
    }

    private IDatabase Database => _connection.GetDatabase(_options.Database);

    public async Task<bool> CreateAsync(JobInfo job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentException.ThrowIfNullOrWhiteSpace(job.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(job.Name);
        cancellationToken.ThrowIfCancellationRequested();
        await SweepIfDueAsync().AnyContext();

        string member = $"{job.CreatedAt.UtcTicks:x16}|{job.Id}";
        var values = new List<RedisValue> { job.Id, job.Name, ToMicroseconds(job.AvailableAt), member };
        values.AddRange(["payload", job.Payload, "created", ToMicroseconds(job.CreatedAt)]);
        AddIfPresent(values, "correlation", job.CorrelationId);
        AddIfPresent(values, "schedule", job.ScheduleName);
        AddIfPresent(values, "started", job.StartedAt);
        AddIfPresent(values, "completed", job.CompletedAt);
        AddIfPresent(values, "progress", job.Progress?.ToString(CultureInfo.InvariantCulture));
        AddIfPresent(values, "message", job.Message);
        AddIfPresent(values, "error", job.Error);
        if (job.CancellationRequested)
            values.AddRange(["cancel", "1"]);

        RedisKey[] keys =
        [
            JobKey(job.Id), QueuedKey(job.Name), IndexKey(null, null), IndexKey(null, job.Name),
            IndexKey(JobStatus.Queued, null), IndexKey(JobStatus.Queued, job.Name)
        ];

        for (int attempt = 1; ; attempt++)
        {
            long created = await EvaluateAsync(CreateScript, keys, values.ToArray()).AnyContext();
            if (created != -1 || attempt == MaxRetries)
                return created == 1;

            // A finished job whose retention ran out still holds the id; remove it, then create the new one.
            await RemoveAsync(job.Id, onlyIfExpired: true).AnyContext();
        }
    }

    public async Task<JobInfo?> GetAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        cancellationToken.ThrowIfCancellationRequested();
        return await ReadJobAsync(jobId).AnyContext();
    }

    public async Task<JobPage> QueryAsync(JobQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Limit, 1, nameof(query));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.Limit, 1000, nameof(query));
        cancellationToken.ThrowIfCancellationRequested();

        string continuation = query.ContinuationToken ?? String.Empty;
        if (continuation.Length > 0 && (continuation.Length < 18 || continuation[16] != '|' || !Int64.TryParse(continuation.AsSpan(0, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _)))
            throw new ArgumentException("The continuation token is not valid.", nameof(query));

        await SweepIfDueAsync().AnyContext();
        var members = (string[])(await Database.ScriptEvaluateAsync(QueryScript, [IndexKey(query.Status, query.Name)], [continuation, query.Limit]).AnyContext())!;
        var page = members.Take(query.Limit).ToArray();
        var jobs = await Task.WhenAll(page.Select(member => ReadJobAsync(member[(member.IndexOf('|') + 1)..]))).AnyContext();
        return new JobPage(jobs.Where(j => j is not null).Select(j => j!).ToList(), members.Length > query.Limit ? page[^1] : null);
    }

    public async Task<long> CountAsync(JobStatus? status = null, string? name = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await SweepAsync(CountSweepLimit).AnyContext();
        return await Database.SortedSetLengthAsync(IndexKey(status, name)).AnyContext();
    }

    public async Task<JobInfo?> ClaimAsync(JobClaimRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Names);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkerId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.LeaseDuration, TimeSpan.Zero, nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Names.Count == 0)
            return null;

        await SweepIfDueAsync().AnyContext();
        string[] names = request.Names.Distinct(StringComparer.Ordinal).ToArray();
        var setKeys = names.SelectMany(n => new RedisKey[] { QueuedKey(n), RunningKey(n) }).ToArray();

        // Find the jobs due longest, then claim the first of them that no other worker has taken in the meantime.
        // Two scripts, because a script can only touch the jobs whose keys it is given.
        for (int round = 0; round < MaxClaimRounds; round++)
        {
            var candidates = (RedisResult[])(await Database.ScriptEvaluateAsync(CandidatesScript, setKeys, [ClaimCandidates]).AnyContext())!;
            if (candidates.Length == 0)
                return null;

            var keys = new List<RedisKey>(2 + 4 * names.Length + candidates.Length / 3) { IndexKey(JobStatus.Queued, null), IndexKey(JobStatus.Running, null) };
            foreach (string name in names)
                keys.AddRange([QueuedKey(name), RunningKey(name), IndexKey(JobStatus.Queued, name), IndexKey(JobStatus.Running, name)]);

            var values = new List<RedisValue>(4 + names.Length + candidates.Length) { request.WorkerId, ToMicroseconds(request.LeaseDuration), Guid.NewGuid().ToString("N"), names.Length };
            values.AddRange(names.Select(n => (RedisValue)n));
            for (int i = 0; i + 2 < candidates.Length; i += 3)
            {
                string id = (string)candidates[i]!;
                keys.Add(JobKey(id));
                values.AddRange([id, (long)candidates[i + 1], (string)candidates[i + 2]!]);
            }

            var claimed = await Database.ScriptEvaluateAsync(ClaimScript, keys.ToArray(), values.ToArray()).AnyContext();
            if (!claimed.IsNull)
                return ToJob((RedisValue[])claimed!);
        }

        return null;
    }

    public async Task<JobLeaseStatus> RenewAsync(string jobId, string claimToken, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (await GetClaimedNameAsync(jobId, claimToken).AnyContext() is not { } name)
            return JobLeaseStatus.Lost;

        return await EvaluateAsync(RenewScript, [JobKey(jobId), RunningKey(name)], [jobId, claimToken, ToMicroseconds(leaseDuration), name]).AnyContext() switch
        {
            1 => JobLeaseStatus.Renewed,
            2 => JobLeaseStatus.CancellationRequested,
            _ => JobLeaseStatus.Lost
        };
    }

    public async Task<bool> ReportProgressAsync(string jobId, string claimToken, int? percent, string? message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);
        cancellationToken.ThrowIfCancellationRequested();

        return await EvaluateAsync(ProgressScript, [JobKey(jobId)],
            [claimToken, percent.HasValue ? 1 : 0, percent ?? 0, message is not null ? 1 : 0, message ?? String.Empty]).AnyContext() == 1;
    }

    public async Task<bool> CompleteAsync(string jobId, string claimToken, JobCompletion completion, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);
        ArgumentNullException.ThrowIfNull(completion);
        cancellationToken.ThrowIfCancellationRequested();
        if (completion.Status == JobStatus.Running)
            throw new ArgumentException("A run cannot complete as Running.", nameof(completion));

        if (await GetClaimedNameAsync(jobId, claimToken).AnyContext() is not { } name)
            return false;

        RedisKey[] keys =
        [
            JobKey(jobId), RunningKey(name), QueuedKey(name), ExpiringKey, IndexKey(JobStatus.Running, null), IndexKey(JobStatus.Running, name),
            IndexKey(completion.Status, null), IndexKey(completion.Status, name)
        ];

        return await EvaluateAsync(CompleteScript, keys, [
            jobId, claimToken, Status(completion.Status), ToMicroseconds(completion.RetryDelay), completion.RefundAttempt ? 1 : 0,
            ToMicroseconds(completion.RetainFor), completion.Message is not null ? 1 : 0, completion.Message ?? String.Empty,
            completion.Error is not null ? 1 : 0, completion.Error ?? String.Empty, name
        ]).AnyContext() == 1;
    }

    public async Task<bool> RequestCancellationAsync(string jobId, TimeSpan retainFor, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        cancellationToken.ThrowIfCancellationRequested();

        for (int attempt = 1; ; attempt++)
        {
            string? name = await Database.HashGetAsync(JobKey(jobId), "name").AnyContext();
            if (name is null)
                return false;

            RedisKey[] keys =
            [
                JobKey(jobId), QueuedKey(name), ExpiringKey, IndexKey(JobStatus.Queued, null), IndexKey(JobStatus.Queued, name),
                IndexKey(JobStatus.Cancelled, null), IndexKey(JobStatus.Cancelled, name)
            ];

            long cancelled = await EvaluateAsync(CancelScript, keys, [jobId, ToMicroseconds(retainFor), name]).AnyContext();
            if (cancelled != -1 || attempt == MaxRetries)
                return cancelled == 1;
        }
    }

    public async Task<bool> DeleteAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        cancellationToken.ThrowIfCancellationRequested();
        return await RemoveAsync(jobId, onlyIfExpired: false).AnyContext() == 1;
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
                    StatusIndexKey(fields[1]!, null), StatusIndexKey(fields[1]!, fields[0])
                ];

            long removed = await EvaluateAsync(RemoveScript, keys,
                [jobId, fields[0].IsNull ? String.Empty : fields[0], fields[1].IsNull ? String.Empty : fields[1], fields[2].IsNull ? String.Empty : fields[2], onlyIfExpired ? 1 : 0]).AnyContext();
            if (removed != -1 || attempt == MaxRetries)
                return removed;
        }
    }

    /// <summary>Removes finished jobs whose retention has run out, at most once a second for calls that only need it eventually.</summary>
    private async Task SweepIfDueAsync()
    {
        if (Environment.TickCount64 < Volatile.Read(ref _nextSweep) || Interlocked.Exchange(ref _sweeping, 1) == 1)
            return;

        try
        {
            Volatile.Write(ref _nextSweep, Environment.TickCount64 + SweepIntervalMilliseconds);
            await SweepAsync(SweepLimit).AnyContext();
        }
        finally
        {
            Volatile.Write(ref _sweeping, 0);
        }
    }

    private async Task SweepAsync(int limit)
    {
        var expired = (string[])(await Database.ScriptEvaluateAsync(ExpiredScript, [ExpiringKey], [limit]).AnyContext())!;
        if (expired.Length > 0)
            await Task.WhenAll(expired.Select(id => RemoveAsync(id, onlyIfExpired: true))).AnyContext();
    }

    private async Task<JobInfo?> ReadJobAsync(string jobId)
    {
        var result = await Database.ScriptEvaluateAsync(GetScript, [JobKey(jobId)]).AnyContext();
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

    private async Task<long> EvaluateAsync(string script, RedisKey[] keys, RedisValue[] values)
    {
        return (long)await Database.ScriptEvaluateAsync(script, keys, values).AnyContext();
    }

    private RedisKey JobKey(string jobId) => _prefix + "job:" + jobId;

    private RedisKey QueuedKey(string name) => _prefix + "queued:" + name;

    private RedisKey RunningKey(string name) => _prefix + "running:" + name;

    private RedisKey ExpiringKey => _prefix + "expiring";

    private RedisKey IndexKey(JobStatus? status, string? name) => status is { } value ? StatusIndexKey(Status(value), name) : name is null ? _prefix + "index" : $"{_prefix}index:name:{name}";

    private RedisKey StatusIndexKey(string status, string? name) => name is null ? $"{_prefix}index:status:{status}" : $"{_prefix}index:status:{status}:{name}";

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
        for (int i = 0; i + 1 < pairs.Length; i += 2)
            fields[pairs[i]!] = pairs[i + 1];

        string? Text(string field) => fields.TryGetValue(field, out var value) ? (string?)value : null;
        DateTimeOffset? Time(string field) => fields.TryGetValue(field, out var value) ? FromMicroseconds((long)value) : null;

        return new JobInfo
        {
            Id = Text("id")!,
            Name = Text("name")!,
            Status = (JobStatus)(int)fields["status"],
            Payload = fields.TryGetValue("payload", out var payload) ? (byte[]?)payload ?? [] : [],
            CorrelationId = Text("correlation"),
            ScheduleName = Text("schedule"),
            Attempt = (int)fields["attempt"],
            CreatedAt = Time("created") ?? default,
            AvailableAt = Time("available") ?? default,
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

    private static long ToMicroseconds(DateTimeOffset time) => (time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / TimeSpan.TicksPerMicrosecond;

    private static long ToMicroseconds(TimeSpan duration) => duration <= TimeSpan.Zero ? 0 : duration.Ticks / TimeSpan.TicksPerMicrosecond;

    private static DateTimeOffset FromMicroseconds(long microseconds) => DateTimeOffset.UnixEpoch.AddTicks(microseconds * TimeSpan.TicksPerMicrosecond);

    private static string Status(JobStatus status) => ((int)status).ToString(CultureInfo.InvariantCulture);

    private static string LoadScript(string name) => Common + EmbeddedResourceLoader.GetEmbeddedResource($"Foundatio.Redis.Scripts.JobStore.{name}.lua");
}

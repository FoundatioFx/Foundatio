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
/// queries and counts. Every operation is a Lua script that reads "now" from the Redis clock.
/// </summary>
public sealed class RedisJobStore : IJobStore
{
    private static readonly string Common =
        $"local QUEUED, RUNNING, SUCCEEDED, CANCELLED = '{Status(JobStatus.Queued)}', '{Status(JobStatus.Running)}', '{Status(JobStatus.Succeeded)}', '{Status(JobStatus.Cancelled)}'\n"
        + EmbeddedResourceLoader.GetEmbeddedResource("Foundatio.Redis.Scripts.JobStore.Common.lua");

    private static readonly string CreateScript = LoadScript("Create");
    private static readonly string GetScript = LoadScript("Get");
    private static readonly string QueryScript = LoadScript("Query");
    private static readonly string CountScript = LoadScript("Count");
    private static readonly string ClaimScript = LoadScript("Claim");
    private static readonly string RenewScript = LoadScript("Renew");
    private static readonly string ProgressScript = LoadScript("Progress");
    private static readonly string CompleteScript = LoadScript("Complete");
    private static readonly string CancelScript = LoadScript("Cancel");
    private static readonly string DeleteScript = LoadScript("Delete");

    private readonly IConnectionMultiplexer _connection;
    private readonly RedisJobStoreOptions _options;
    private readonly string _prefix;

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

        var values = new List<RedisValue> { _prefix, job.Id, job.Name, ToMicroseconds(job.AvailableAt), $"{job.CreatedAt.UtcTicks:x16}|{job.Id}" };
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

        return await EvaluateAsync(CreateScript, JobKey(job.Id), values.ToArray()).AnyContext() == 1;
    }

    public async Task<JobInfo?> GetAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        cancellationToken.ThrowIfCancellationRequested();

        var result = await Database.ScriptEvaluateAsync(GetScript, [JobKey(jobId)], [_prefix]).AnyContext();
        return result.IsNull ? null : ToJob((RedisValue[])result!);
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

        var result = (RedisResult[])(await Database.ScriptEvaluateAsync(QueryScript, [IndexKey(query.Status, query.Name)], [_prefix, continuation, query.Limit]).AnyContext())!;
        var jobs = ((RedisResult[])result[0]!).Select(j => ToJob((RedisValue[])j!)).ToList();
        string? next = (string?)result[1];
        return new JobPage(jobs, String.IsNullOrEmpty(next) ? null : next);
    }

    public async Task<long> CountAsync(JobStatus? status = null, string? name = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (long)await Database.ScriptEvaluateAsync(CountScript, [IndexKey(status, name)], [_prefix]).AnyContext();
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

        var values = new List<RedisValue>(4 + request.Names.Count) { _prefix, request.WorkerId, ToMicroseconds(request.LeaseDuration), Guid.NewGuid().ToString("N") };
        values.AddRange(request.Names.Select(n => (RedisValue)n));

        var result = await Database.ScriptEvaluateAsync(ClaimScript, [_prefix + "queued:" + request.Names.First()], values.ToArray()).AnyContext();
        return result.IsNull ? null : ToJob((RedisValue[])result!);
    }

    public async Task<JobLeaseStatus> RenewAsync(string jobId, string claimToken, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);
        cancellationToken.ThrowIfCancellationRequested();

        return await EvaluateAsync(RenewScript, JobKey(jobId), [_prefix, jobId, claimToken, ToMicroseconds(leaseDuration)]).AnyContext() switch
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

        return await EvaluateAsync(ProgressScript, JobKey(jobId),
            [_prefix, claimToken, percent.HasValue ? 1 : 0, percent ?? 0, message is not null ? 1 : 0, message ?? String.Empty]).AnyContext() == 1;
    }

    public async Task<bool> CompleteAsync(string jobId, string claimToken, JobCompletion completion, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);
        ArgumentNullException.ThrowIfNull(completion);
        cancellationToken.ThrowIfCancellationRequested();
        if (completion.Status == JobStatus.Running)
            throw new ArgumentException("A run cannot complete as Running.", nameof(completion));

        return await EvaluateAsync(CompleteScript, JobKey(jobId), [
            _prefix, jobId, claimToken, Status(completion.Status), ToMicroseconds(completion.RetryDelay), completion.RefundAttempt ? 1 : 0,
            ToMicroseconds(completion.RetainFor), completion.Message is not null ? 1 : 0, completion.Message ?? String.Empty,
            completion.Error is not null ? 1 : 0, completion.Error ?? String.Empty
        ]).AnyContext() == 1;
    }

    public async Task<bool> RequestCancellationAsync(string jobId, TimeSpan retainFor, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        cancellationToken.ThrowIfCancellationRequested();
        return await EvaluateAsync(CancelScript, JobKey(jobId), [_prefix, jobId, ToMicroseconds(retainFor)]).AnyContext() == 1;
    }

    public async Task<bool> DeleteAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        cancellationToken.ThrowIfCancellationRequested();
        return await EvaluateAsync(DeleteScript, JobKey(jobId), [_prefix, jobId]).AnyContext() == 1;
    }

    private async Task<long> EvaluateAsync(string script, RedisKey key, RedisValue[] values)
    {
        return (long)await Database.ScriptEvaluateAsync(script, [key], values).AnyContext();
    }

    private RedisKey JobKey(string jobId) => _prefix + "job:" + jobId;

    private RedisKey IndexKey(JobStatus? status, string? name) => (status, name) switch
    {
        (null, null) => _prefix + "index",
        (null, _) => $"{_prefix}index:name:{name}",
        (_, null) => $"{_prefix}index:status:{Status(status.Value)}",
        _ => $"{_prefix}index:status:{Status(status.Value)}:{name}"
    };

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

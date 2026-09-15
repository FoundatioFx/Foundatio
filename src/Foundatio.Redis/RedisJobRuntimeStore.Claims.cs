using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using StackExchange.Redis;

namespace Foundatio.Jobs;

public sealed partial class RedisJobRuntimeStore
{
    public async Task<JobOccurrenceResult> CreateOccurrenceAsync(JobState initial, bool allowOverlap = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentException.ThrowIfNullOrWhiteSpace(initial.ScheduleName);
        if (initial.ExecutionOwner != JobExecutionOwner.Runtime) throw new ArgumentException("Scheduled occurrences must be owned by the job runtime.", nameof(initial));
        ArgumentException.ThrowIfNullOrWhiteSpace(initial.JobType);
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePayload(initial.Payload?.Length ?? 0);
        initial.RetryPolicy.Validate();
        var now = _timeProvider.GetUtcNow();
        var state = initial with { CreatedUtc = initial.CreatedUtc == default ? now : initial.CreatedUtc, LastUpdatedUtc = now };
        const string script = MonitoringFunctions + "\n" + """
            if redis.call('EXISTS', KEYS[1]) == 1 then return 0 end
            if ARGV[2] == '0' and redis.call('SCARD', KEYS[6]) > 0 then return 2 end
            redis.call('ZREMRANGEBYSCORE', KEYS[7], '-inf', ARGV[5])
            if redis.call('ZSCORE', KEYS[7], ARGV[1]) then return 0 end
            if redis.call('ZCARD', KEYS[2]) - redis.call('ZCARD', KEYS[8]) >= tonumber(ARGV[4]) then return -1 end
            if redis.call('ZCARD', KEYS[7]) >= tonumber(ARGV[6]) then return -2 end
            redis.call('HSET', KEYS[1], unpack(ARGV, 7))
            redis.call('ZADD', KEYS[7], '+inf', ARGV[1])
            local expiry = redis.call('HGET', KEYS[1], 'expiresUtc')
            if expiry then redis.call('ZADD', KEYS[9], expiry, ARGV[1]) end
            redis.call('ZADD', KEYS[2], 0, ARGV[1])
            redis.call('ZADD', KEYS[3], 0, ARGV[1])
            redis.call('ZADD', KEYS[4], 0, ARGV[1])
            redis.call('ZADD', KEYS[5], ARGV[3], ARGV[1])
            redis.call('SADD', KEYS[6], ARGV[1])
            syncMonitoring(KEYS[1])
            return 1
            """;
        var arguments = new List<RedisValue> { state.JobId, allowOverlap ? "1" : "0", Ticks(state.AvailableUtc ?? state.CreatedUtc), _options.MaxActiveJobs, Ticks(now), _options.MaxDeduplicationRecords };
        foreach (var field in ToHash(state))
        {
            arguments.Add(field.Name);
            arguments.Add(field.Value);
        }

        var result = await _db.ScriptEvaluateAsync(script,
            new RedisKey[] { JobKey(state.JobId), AllKey, StatusKey(state.Status), NameKey(state.Name), ReadyKey(state.JobType, state.RequiredNodeId), ActiveScheduleKey(state.ScheduleName, state.RequiredNodeId), DeduplicationKey, TerminalKey, UnclaimedKey },
            arguments.ToArray()).ConfigureAwait(false);
        ThrowIfCapacityExceeded((long)result);
        return (JobOccurrenceResult)(int)result;
    }

    private const string ClaimJobScript = RetentionFunctions + "\n" + """
        local now = tonumber(ARGV[1])
        for scan = 1, 100 do
            local id, key, score
            for _, candidateKey in ipairs(KEYS) do
                local candidate
                if ARGV[6] ~= '' then
                    local value = redis.call('ZSCORE', candidateKey, ARGV[6])
                    if value then candidate = {ARGV[6], value} else candidate = {} end
                else
                    candidate = redis.call('ZRANGE', candidateKey, 0, 0, 'WITHSCORES')
                end
                if #candidate > 0 then
                    local due = tonumber(candidate[2])
                    if due <= now and (not score or due < score or (due == score and candidate[1] < id)) then
                        id, key, score = candidate[1], candidateKey, due
                    end
                end
            end
            if not id then return {} end
            local job = ARGV[5] .. 'job:' .. id
            local status = redis.call('HGET', job, 'status')
            if redis.call('HGET', job, 'executionOwner') == 'Broker' or (status ~= 'Queued' and status ~= 'Scheduled' and status ~= 'Processing') then
                redis.call('ZREM', key, id)
            else
                local due = redis.call('HGET', job, status == 'Processing' and 'leaseExpiresUtc' or 'availableUtc')
                if not due or due == '' then due = redis.call('HGET', job, 'createdUtc') end
                if tonumber(due) > now then
                    redis.call('ZADD', key, due, id)
                else
                    local attempt = tonumber(redis.call('HGET', job, 'attempt') or '0')
                    local maximum = tonumber(redis.call('HGET', job, 'maxAttempts') or '3')
                    local expiry = tonumber(redis.call('HGET', job, 'expiresUtc'))
                    local expired = attempt == 0 and redis.call('HEXISTS', job, 'requiredNodeId') == 1 and expiry and expiry <= now
                    local cancelled = expired or redis.call('HGET', job, 'cancellationRequested') == '1'
                    if expired then redis.call('HSET', job, 'resultMessage', 'Unclaimed per-node occurrence expired.') end
                    redis.call('ZREM', ARGV[5] .. 'status:' .. status, id)
                    if cancelled or attempt >= maximum then
                        local terminal = cancelled and 'Cancelled' or 'Failed'
                        redis.call('HSET', job, 'status', terminal, 'completedUtc', ARGV[1], 'lastUpdatedUtc', ARGV[1])
                        redis.call('HDEL', job, 'nodeId', 'claimToken', 'leaseExpiresUtc')
                        if not cancelled then redis.call('HSET', job, 'error', 'Execution attempts exhausted after lease expiration.') end
                        redis.call('ZADD', ARGV[5] .. 'status:' .. terminal, 0, id)
                        redis.call('ZADD', ARGV[5] .. 'terminal', ARGV[1], id)
                        redis.call('ZREM', key, id)
                        local active = redis.call('HGET', job, 'activeScheduleKey')
                        if active then redis.call('SREM', active, id) end
                        syncMonitoring(job)
                        finishJob(job, id, ARGV[5], now, tonumber(ARGV[7]), tonumber(ARGV[8]), tonumber(ARGV[9]))
                    else
                        redis.call('HSET', job, 'status', 'Processing', 'nodeId', ARGV[2], 'claimToken', ARGV[3],
                            'leaseExpiresUtc', ARGV[4], 'startedUtc', ARGV[1], 'lastUpdatedUtc', ARGV[1], 'attempt', attempt + 1)
                        redis.call('HDEL', job, 'completedUtc')
                        redis.call('ZADD', ARGV[5] .. 'status:Processing', 0, id)
                        redis.call('ZADD', key, ARGV[4], id)
                        redis.call('ZREM', ARGV[5] .. 'unclaimed', id)
                        syncMonitoring(job)
                        return redis.call('HGETALL', job)
                    end
                end
            end
        end
        return {}
        """;

    private const string CompleteJobScript = RetentionFunctions + "\n" + """
        if expireJob(KEYS[1], tonumber(ARGV[2])) then return 0 end
        if redis.call('HGET', KEYS[1], 'status') ~= 'Processing' or redis.call('HGET', KEYS[1], 'claimToken') ~= ARGV[1] then return 0 end
        if redis.call('HGET', KEYS[1], 'executionOwner') == 'Broker' then
            local kind = tonumber(ARGV[3])
            local status = kind == 0 and 'Completed' or kind == 2 and 'Cancelled' or (kind == 3 or (kind == 1 and ARGV[7] == '1')) and 'RetryPending' or 'Failed'
            local id = redis.call('HGET', KEYS[1], 'jobId')
            redis.call('ZREM', ARGV[5] .. 'status:Processing', id)
            redis.call('ZADD', ARGV[5] .. 'status:' .. status, 0, id)
            redis.call('HSET', KEYS[1], 'status', status, 'lastUpdatedUtc', ARGV[2], 'error', ARGV[4], 'resultMessage', ARGV[6])
            redis.call('HDEL', KEYS[1], 'claimToken', 'leaseExpiresUtc', 'availableUtc')
            refreshBrokerHistory(KEYS[1], tonumber(ARGV[2]))
            syncMonitoring(KEYS[1])
            if status ~= 'RetryPending' then
                redis.call('HSET', KEYS[1], 'completedUtc', ARGV[2])
                if status == 'Completed' then redis.call('HSET', KEYS[1], 'progress', 100) end
                redis.call('ZADD', ARGV[5] .. 'terminal', ARGV[2], id)
                finishJob(KEYS[1], id, ARGV[5], tonumber(ARGV[2]), tonumber(ARGV[9]), tonumber(ARGV[10]), tonumber(ARGV[11]))
            end
            return 1
        end
        if tonumber(redis.call('HGET', KEYS[1], 'leaseExpiresUtc') or '0') <= tonumber(ARGV[2]) then return 0 end
        local kind = tonumber(ARGV[3])
        if redis.call('HGET', KEYS[1], 'cancellationRequested') == '1' then kind = 2 end
        local attempt = tonumber(redis.call('HGET', KEYS[1], 'attempt') or '0')
        local maximum = tonumber(redis.call('HGET', KEYS[1], 'maxAttempts') or '3')
        local retry = kind == 1 and attempt < maximum and ARGV[7] == '1'
        local status = kind == 0 and 'Completed' or kind == 2 and 'Cancelled' or (kind == 3 or retry) and 'Queued' or 'Failed'
        local available = ARGV[2]
        if retry then
            local initial = tonumber(redis.call('HGET', KEYS[1], 'retryInitialSeconds') or '10')
            local maximumDelay = tonumber(redis.call('HGET', KEYS[1], 'retryMaxSeconds') or '300')
            local multiplier = tonumber(redis.call('HGET', KEYS[1], 'retryMultiplier') or '2')
            local jitter = tonumber(redis.call('HGET', KEYS[1], 'retryJitter') or '0.2')
            local baseDelay = initial == 0 and 0 or math.min(maximumDelay, initial * multiplier ^ math.min(100, attempt - 1))
            local seconds = math.min(maximumDelay, baseDelay * (1 + jitter * (2 * tonumber(ARGV[8]) - 1)))
            available = string.format('%.0f', tonumber(ARGV[2]) + seconds * 10000000)
        end
        redis.call('HSET', KEYS[1], 'status', status, 'lastUpdatedUtc', ARGV[2], 'availableUtc', available)
        redis.call('HDEL', KEYS[1], 'nodeId', 'claimToken', 'leaseExpiresUtc')
        if ARGV[4] ~= '' then redis.call('HSET', KEYS[1], 'error', ARGV[4]) else redis.call('HDEL', KEYS[1], 'error') end
        if ARGV[6] ~= '' then redis.call('HSET', KEYS[1], 'resultMessage', ARGV[6]) else redis.call('HDEL', KEYS[1], 'resultMessage') end
        if status == 'Queued' then redis.call('HDEL', KEYS[1], 'completedUtc') else redis.call('HSET', KEYS[1], 'completedUtc', ARGV[2]) end
        if status == 'Completed' then redis.call('HSET', KEYS[1], 'progress', '100') end
        local id = redis.call('HGET', KEYS[1], 'jobId')
        local ready = redis.call('HGET', KEYS[1], 'readyKey')
        if status == 'Queued' then redis.call('ZADD', ready, available, id) else redis.call('ZREM', ready, id) end
        if status ~= 'Queued' then
            local active = redis.call('HGET', KEYS[1], 'activeScheduleKey')
            if active then redis.call('SREM', active, id) end
        end
        redis.call('ZREM', ARGV[5] .. 'status:Processing', id)
        redis.call('ZADD', ARGV[5] .. 'status:' .. status, 0, id)
        if status ~= 'Queued' then
            redis.call('ZADD', ARGV[5] .. 'terminal', ARGV[2], id)
            finishJob(KEYS[1], id, ARGV[5], tonumber(ARGV[2]), tonumber(ARGV[9]), tonumber(ARGV[10]), tonumber(ARGV[11]))
        end
        syncMonitoring(KEYS[1])
        return 1
        """;

    private const string RenewJobLeaseScript = """
        if redis.call('HGET', KEYS[1], 'status') ~= 'Processing' or redis.call('HGET', KEYS[1], 'claimToken') ~= ARGV[1] then return 0 end
        if tonumber(redis.call('HGET', KEYS[1], 'leaseExpiresUtc') or '0') <= tonumber(ARGV[2]) then return 0 end
        redis.call('HSET', KEYS[1], 'leaseExpiresUtc', ARGV[3], 'lastUpdatedUtc', ARGV[2])
        local id = redis.call('HGET', KEYS[1], 'jobId')
        redis.call('ZADD', redis.call('HGET', KEYS[1], 'readyKey'), ARGV[3], id)
        return 1
        """;

    private const string ReportJobProgressScript = MonitoringFunctions + "\n" + """
        if expireJob(KEYS[1], tonumber(ARGV[2])) then return 0 end
        if redis.call('HGET', KEYS[1], 'status') ~= 'Processing' or redis.call('HGET', KEYS[1], 'claimToken') ~= ARGV[1] then return 0 end
        if redis.call('HGET', KEYS[1], 'executionOwner') ~= 'Broker' and tonumber(redis.call('HGET', KEYS[1], 'leaseExpiresUtc') or '0') <= tonumber(ARGV[2]) then return 0 end
        if ARGV[3] ~= '' then redis.call('HSET', KEYS[1], 'progress', ARGV[3]) end
        if ARGV[4] == '1' then redis.call('HSET', KEYS[1], 'progressMessage', ARGV[5]) end
        redis.call('HSET', KEYS[1], 'lastUpdatedUtc', ARGV[2], 'lastHeartbeatUtc', ARGV[2])
        refreshBrokerHistory(KEYS[1], tonumber(ARGV[2]))
        syncMonitoring(KEYS[1])
        return 1
        """;

    public Task<JobState?> ClaimNextAsync(JobClaimRequest request, CancellationToken cancellationToken = default)
        => ClaimJobCoreAsync(null, request, cancellationToken);

    public Task<JobState?> ClaimJobAsync(string jobId, JobClaimRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        return ClaimJobCoreAsync(jobId, request, cancellationToken);
    }

    private async Task<JobState?> ClaimJobCoreAsync(string? jobId, JobClaimRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.NodeId);
        ArgumentNullException.ThrowIfNull(request.JobTypes);
        if (request.JobTypes.Count == 0 || request.JobTypes.Any(String.IsNullOrWhiteSpace))
            throw new ArgumentException("Register the job types this worker can execute.", nameof(request));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.Lease, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();
        var now = _timeProvider.GetUtcNow();
        var result = await _db.ScriptEvaluateAsync(ClaimJobScript, request.JobTypes.Distinct(StringComparer.Ordinal).SelectMany(t => new[] { ReadyKey(t), ReadyKey(t, request.NodeId) }).ToArray(),
            new RedisValue[] { Ticks(now), request.NodeId, Guid.NewGuid().ToString("N"), Ticks(now.Add(request.Lease)), _prefix, jobId ?? "", _options.MaxHistoryJobs, _options.HistoryRetention.Ticks, _options.DeduplicationRetention.Ticks }).ConfigureAwait(false);
        var values = (RedisResult[])result!;
        if (values.Length == 0)
            return null;
        return ReadJobSnapshot(result);
    }

    public Task<bool> CompleteJobAsync(string jobId, string claimToken, JobCompletion completion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completion);
        if (!Enum.IsDefined(completion.Kind))
            throw new ArgumentOutOfRangeException(nameof(completion));
        return MutateClaimAsync(CompleteJobScript, jobId, claimToken,
            new RedisValue[] { (int)completion.Kind, completion.Kind == JobCompletionKind.Failed ? completion.Error ?? "" : "", _prefix, completion.Message ?? "", completion.Retryable ? "1" : "0", Random.Shared.NextDouble(), _options.MaxHistoryJobs, _options.HistoryRetention.Ticks, _options.DeduplicationRetention.Ticks }, cancellationToken);
    }

    public Task<bool> RenewJobLeaseAsync(string jobId, string claimToken, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
        return MutateClaimAsync(RenewJobLeaseScript, jobId, claimToken,
            new RedisValue[] { Ticks(_timeProvider.GetUtcNow().Add(lease)), _prefix }, cancellationToken);
    }

    public Task<bool> ReportJobProgressAsync(string jobId, string claimToken, int? percent = null, string? message = null, CancellationToken cancellationToken = default)
    {
        if (percent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(percent));
        return MutateClaimAsync(ReportJobProgressScript, jobId, claimToken,
            new RedisValue[] { percent?.ToString(CultureInfo.InvariantCulture) ?? "", message is null ? "0" : "1", message ?? "" }, cancellationToken);
    }

    private async Task<bool> MutateClaimAsync(string script, string jobId, string claimToken, RedisValue[] arguments, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);
        cancellationToken.ThrowIfCancellationRequested();
        RedisValue[] values = [claimToken, Ticks(_timeProvider.GetUtcNow()), .. arguments];
        var result = await _db.ScriptEvaluateAsync(script, new RedisKey[] { JobKey(jobId) }, values).ConfigureAwait(false);
        return (long)result == 1;
    }

    private RedisKey ReadyKey(string jobType, string? nodeId = null) => $"{_prefix}ready:{EncodeKey(jobType)}:{EncodeKey(nodeId ?? "")}";
    private RedisKey ActiveScheduleKey(string name, string? nodeId) => $"{_prefix}active-schedule:{EncodeKey(name)}:{EncodeKey(nodeId ?? "")}";
    private static string EncodeKey(string value) => Convert.ToHexString(Encoding.UTF8.GetBytes(value));
}

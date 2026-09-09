using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StackExchange.Redis;

namespace Foundatio.Jobs;

public sealed partial class RedisJobRuntimeStore
{
    public bool IsShared => true;

    private const string MonitoringFunctions = """
        local function hex(value)
            return (string.gsub(value, '.', function(c) return string.format('%02X', string.byte(c)) end))
        end
        local function monitoringKeys(job, status)
            local id = redis.call('HGET', job, 'jobId')
            local prefix = string.sub(job, 1, #job - #id - 4)
            local keys = {prefix .. 'created', prefix .. 'created-status:' .. status}
            local name = redis.call('HGET', job, 'monitorName')
            if not name then
                local value = redis.call('HGET', job, 'name')
                if value then name = hex(value); redis.call('HSET', job, 'monitorName', name) end
            end
            if name then
                table.insert(keys, prefix .. 'created-name:' .. name)
                table.insert(keys, prefix .. 'created-name:' .. name .. ':' .. status)
            end
            local queue = redis.call('HGET', job, 'monitorQueue')
            if not queue then
                local value = redis.call('HGET', job, 'queueName')
                if value then queue = hex(value); redis.call('HSET', job, 'monitorQueue', queue) end
            end
            if queue then
                table.insert(keys, prefix .. 'created-queue:' .. queue)
                table.insert(keys, prefix .. 'created-queue:' .. queue .. ':' .. status)
            end
            return keys, prefix, id
        end
        local function removeMonitoring(job)
            local status = redis.call('HGET', job, 'monitorStatus') or redis.call('HGET', job, 'status')
            if not status then return end
            local keys, prefix, id = monitoringKeys(job, status)
            for _, key in ipairs(keys) do redis.call('ZREM', key, id) end
            redis.call('ZREM', prefix .. 'broker-expiry', id)
        end
        local function syncMonitoring(job)
            local values = redis.call('HMGET', job, 'status', 'monitorStatus', 'createdUtc', 'historyExpiresUtc', 'monitorExpiry', 'jobId')
            local status, previous = values[1], values[2]
            if not status then return end
            local id = values[6]
            local prefix = string.sub(job, 1, #job - #id - 4)
            if previous ~= status then
                local keys = monitoringKeys(job, status)
                for i = 1, #keys, 2 do
                    if not previous then redis.call('ZADD', keys[i], values[3], id)
                    else
                        local oldKey = keys[i] .. (i == 1 and '-status:' or ':') .. previous
                        redis.call('ZREM', oldKey, id)
                    end
                    redis.call('ZADD', keys[i+1], values[3], id)
                end
                redis.call('HSET', job, 'monitorStatus', status)
            end
            if values[4] and values[4] ~= values[5] then
                redis.call('ZADD', prefix .. 'broker-expiry', values[4], id)
                redis.call('HSET', job, 'monitorExpiry', values[4])
            end
        end
        local function refreshBrokerHistory(job, now)
            if redis.call('HGET', job, 'executionOwner') ~= 'Broker' then return end
            local retention = tonumber(redis.call('HGET', job, 'historyRetention') or '0')
            if retention > 0 then redis.call('HSET', job, 'historyExpiresUtc', string.format('%.0f', now + retention)) end
        end
        local function forgetJob(job, prefix, id)
            removeMonitoring(job)
            local status, name = redis.call('HGET', job, 'status'), redis.call('HGET', job, 'name')
            if status then redis.call('ZREM', prefix .. 'status:' .. status, id) end
            if name then redis.call('ZREM', prefix .. 'name:' .. name, id) end
            for _, suffix in ipairs({'all', 'terminal', 'unclaimed'}) do redis.call('ZREM', prefix .. suffix, id) end
            if redis.call('HGET', job, 'executionOwner') == 'Broker' then redis.call('ZREM', prefix .. 'deduplication', id) end
            local ready, schedule = redis.call('HGET', job, 'readyKey'), redis.call('HGET', job, 'activeScheduleKey')
            if ready then redis.call('ZREM', ready, id) end
            if schedule then redis.call('SREM', schedule, id) end
            redis.call('DEL', job)
        end
        local function expireJob(job, now)
            local expires = tonumber(redis.call('HGET', job, 'historyExpiresUtc'))
            if not expires or expires > now or redis.call('HGET', job, 'executionOwner') ~= 'Broker' then return false end
            local _, prefix, id = monitoringKeys(job, redis.call('HGET', job, 'status'))
            forgetJob(job, prefix, id)
            return true
        end
        local function purgeBrokerHistory(prefix, now, limit)
            local expiry = prefix .. 'broker-expiry'
            local ids = redis.call('ZRANGEBYSCORE', expiry, '-inf', now, 'LIMIT', 0, limit)
            for _, id in ipairs(ids) do
                local job = prefix .. 'job:' .. id
                if redis.call('HGET', job, 'executionOwner') == 'Broker' then forgetJob(job, prefix, id) end
                redis.call('ZREM', expiry, id)
            end
            return #ids
        end
        """;

    public async Task<JobState?> BeginBrokerAttemptAsync(string jobId, int attempt, string nodeId, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        const string script = MonitoringFunctions + "\n" + """
            if expireJob(KEYS[1], tonumber(ARGV[4])) then return {} end
            if redis.call('HGET', KEYS[1], 'executionOwner') ~= 'Broker' then return {} end
            local status = redis.call('HGET', KEYS[1], 'status')
            if status ~= 'Queued' and status ~= 'Processing' and status ~= 'RetryPending' and status ~= 'EnqueueUnknown' then return {} end
            if tonumber(redis.call('HGET', KEYS[1], 'attempt') or '0') >= tonumber(ARGV[1]) then return {} end
            redis.call('ZREM', ARGV[5] .. 'status:' .. status, ARGV[6])
            redis.call('ZADD', ARGV[5] .. 'status:Processing', 0, ARGV[6])
            redis.call('HSET', KEYS[1], 'status', 'Processing', 'attempt', ARGV[1], 'nodeId', ARGV[2], 'claimToken', ARGV[3],
                'startedUtc', ARGV[4], 'lastHeartbeatUtc', ARGV[4], 'lastUpdatedUtc', ARGV[4], 'progress', 0)
            redis.call('HDEL', KEYS[1], 'progressMessage', 'completedUtc')
            refreshBrokerHistory(KEYS[1], tonumber(ARGV[4]))
            syncMonitoring(KEYS[1])
            return redis.call('HGETALL', KEYS[1])
            """;
        var raw = await _db.ScriptEvaluateAsync(script, [JobKey(jobId)], [attempt, nodeId, Guid.NewGuid().ToString("N"), Ticks(_timeProvider.GetUtcNow()), _prefix, jobId]).WaitAsync(cancellationToken).ConfigureAwait(false);
        return ((RedisResult[])raw!).Length == 0 ? null : ReadJobSnapshot(raw);
    }

    public async Task<bool> MarkEnqueueUnknownAsync(string jobId, string error, CancellationToken cancellationToken = default)
    {
        const string script = MonitoringFunctions + "\n" + """
            if expireJob(KEYS[1], tonumber(ARGV[2])) then return 0 end
            if redis.call('HGET', KEYS[1], 'executionOwner') ~= 'Broker' or redis.call('HGET', KEYS[1], 'status') ~= 'Queued' or redis.call('HGET', KEYS[1], 'attempt') ~= '0' then return 0 end
            redis.call('HSET', KEYS[1], 'status', 'EnqueueUnknown', 'error', ARGV[1], 'lastUpdatedUtc', ARGV[2])
            redis.call('ZREM', ARGV[3] .. 'status:Queued', ARGV[4])
            redis.call('ZADD', ARGV[3] .. 'status:EnqueueUnknown', 0, ARGV[4])
            refreshBrokerHistory(KEYS[1], tonumber(ARGV[2]))
            syncMonitoring(KEYS[1])
            return 1
            """;
        return (long)await _db.ScriptEvaluateAsync(script, [JobKey(jobId)], [error, Ticks(_timeProvider.GetUtcNow()), _prefix, jobId]).WaitAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public Task<bool> HeartbeatJobAsync(string jobId, string claimToken, CancellationToken cancellationToken = default)
        => ReportJobProgressAsync(jobId, claimToken, cancellationToken: cancellationToken);

    public async Task<bool> RemoveAsync(string jobId, CancellationToken cancellationToken = default)
    {
        const string script = MonitoringFunctions + "\n" + """
            local status = redis.call('HGET', KEYS[1], 'status')
            if not status then return 0 end
            if redis.call('HGET', KEYS[1], 'executionOwner') ~= 'Broker' and (status == 'Queued' or status == 'Scheduled' or status == 'Processing') then return 0 end
            forgetJob(KEYS[1], ARGV[1], ARGV[2])
            return 1
            """;
        return (long)await _db.ScriptEvaluateAsync(script, [JobKey(jobId)], [_prefix, jobId]).WaitAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private async Task PurgeBrokerHistoryAsync(CancellationToken cancellationToken)
    {
        const string script = MonitoringFunctions + "\n" + """
            return purgeBrokerHistory(ARGV[2], tonumber(ARGV[1]), 128)
            """;
        while ((long)await _db.ScriptEvaluateAsync(script, [$"{_prefix}broker-expiry"], [Ticks(_timeProvider.GetUtcNow()), _prefix]).WaitAsync(cancellationToken).ConfigureAwait(false) == 128)
            cancellationToken.ThrowIfCancellationRequested();
    }

    private RedisKey MonitoringIndex(JobQuery query)
    {
        string scope = query.QueueName is { } queue ? "created-queue:" + EncodeKey(queue) : query.Name is { } name ? "created-name:" + EncodeKey(name) : "created";
        return $"{_prefix}{scope}{(query.Status is { } status ? (scope == "created" ? "-status:" : ":") + status : "")}";
    }

    public async Task<long> CountAsync(JobQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        await PurgeBrokerHistoryAsync(cancellationToken).ConfigureAwait(false);
        if (query.QueueName is null || query.Name is null)
            return await _db.SortedSetLengthAsync(MonitoringIndex(query)).WaitAsync(cancellationToken).ConfigureAwait(false);
        const string script = """
            local count, offset = 0, 0
            repeat
                local ids = redis.call('ZRANGE', KEYS[1], offset, offset + 199)
                for _, id in ipairs(ids) do
                    if redis.call('HGET', ARGV[1] .. 'job:' .. id, 'name') == ARGV[2] then count = count + 1 end
                end
                offset = offset + #ids
            until #ids < 200
            return count
            """;
        return (long)await _db.ScriptEvaluateAsync(script, [MonitoringIndex(query)], [_prefix, query.Name]).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<JobPage> QueryNewestAsync(JobQuery query, CancellationToken cancellationToken)
    {
        const string script = """
            local result, offset, skipped = {}, 0, 0
            repeat
                local ids = redis.call('ZREVRANGE', KEYS[1], offset, offset + 199)
                for _, id in ipairs(ids) do
                    local job = ARGV[1] .. 'job:' .. id
                    if ARGV[2] == '' or redis.call('HGET', job, 'name') == ARGV[2] then
                        if skipped < tonumber(ARGV[3]) then skipped = skipped + 1
                        else table.insert(result, redis.call('HGETALL', job)) end
                        if #result >= tonumber(ARGV[4]) then return result end
                    end
                end
                offset = offset + #ids
            until #ids < 200
            return result
            """;
        var result = (RedisResult[])(await _db.ScriptEvaluateAsync(script, [MonitoringIndex(query)], [_prefix, query.Name ?? "", query.Skip, query.Limit]).WaitAsync(cancellationToken).ConfigureAwait(false))!;
        return new JobPage(result.Select(ReadJobSnapshot).ToArray(), null);
    }

    public Task IncrementCounterAsync(string name, string counterName, long value = 1, CancellationToken cancellationToken = default)
        => _db.ScriptEvaluateAsync("redis.call('HINCRBY', KEYS[1], ARGV[1], ARGV[2]); redis.call('PEXPIRE', KEYS[1], 172800000); return 1",
            [CounterKey(name, _timeProvider.GetUtcNow())], [counterName, value]).WaitAsync(cancellationToken);

    public async Task<JobCounterStats> GetCounterStatsAsync(string name, TimeSpan? window = null, CancellationToken cancellationToken = default)
    {
        var duration = window ?? TimeSpan.FromHours(24);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(duration, TimeSpan.FromHours(48));
        var now = _timeProvider.GetUtcNow();
        var hours = new List<DateTimeOffset>();
        for (var hour = TruncateHour(now - duration); hour <= TruncateHour(now); hour = hour.AddHours(1)) hours.Add(hour);
        var snapshots = await Task.WhenAll(hours.Select(hour => _db.HashGetAllAsync(CounterKey(name, hour)))).WaitAsync(cancellationToken).ConfigureAwait(false);
        var totals = new Dictionary<string, long>();
        var buckets = new List<JobCounterBucket>();
        for (int i = 0; i < hours.Count; i++)
        {
            var counters = snapshots[i].ToDictionary(pair => (string)pair.Name!, pair => (long)pair.Value);
            foreach (var pair in counters) totals[pair.Key] = totals.GetValueOrDefault(pair.Key) + pair.Value;
            buckets.Add(new() { Hour = hours[i], Counters = counters });
        }
        return new JobCounterStats { Totals = totals, Buckets = buckets };
    }

    private RedisKey CounterKey(string name, DateTimeOffset timestamp) => $"{_prefix}counters:{EncodeKey(name)}:{timestamp:yyyy-MM-ddTHH}";
    private static DateTimeOffset TruncateHour(DateTimeOffset value) => new(value.Year, value.Month, value.Day, value.Hour, 0, 0, TimeSpan.Zero);
}

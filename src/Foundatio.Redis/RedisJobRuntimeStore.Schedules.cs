using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StackExchange.Redis;

namespace Foundatio.Jobs;

public sealed partial class RedisJobRuntimeStore
{
    private const string SaveScheduleScript = """
        local revision = tonumber(redis.call('HGET', KEYS[1], 'revision') or '0')
        local version = tonumber(redis.call('HGET', KEYS[1], 'configurationVersion') or '0')
        local incoming = cjson.decode(ARGV[1])
        if ARGV[2] == '1' then
            if incoming.ConfigurationVersion < version then return 0 end
            if incoming.ConfigurationVersion == version then
                if redis.call('HGET', KEYS[1], 'configuration') ~= ARGV[1] then return -2 end
                return 0
            end
            redis.call('HSET', KEYS[1], 'configuration', ARGV[1])
            version = incoming.ConfigurationVersion
        elseif incoming.Revision ~= revision then
            return -1
        end
        incoming.Revision = revision + 1
        incoming.ConfigurationVersion = version
        redis.call('HSET', KEYS[1], 'definition', cjson.encode(incoming), 'revision', revision + 1, 'configurationVersion', version)
        redis.call('ZADD', KEYS[2], 0, ARGV[3])
        return 1
        """;

    public Task ScheduleAsync(ScheduledJobDefinition definition, CancellationToken cancellationToken = default)
        => SaveScheduleAsync(definition, false, cancellationToken);

    public Task ReconcileAsync(ScheduledJobDefinition definition, CancellationToken cancellationToken = default)
        => SaveScheduleAsync(definition, true, cancellationToken);

    private async Task SaveScheduleAsync(ScheduledJobDefinition definition, bool reconcile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        if (reconcile)
            ArgumentOutOfRangeException.ThrowIfLessThan(definition.ConfigurationVersion, 1);
        cancellationToken.ThrowIfCancellationRequested();
        var result = await _db.ScriptEvaluateAsync(SaveScheduleScript, [ScheduleKey(definition.Name), SchedulesKey],
            new RedisValue[] { JsonSerializer.Serialize(reconcile ? definition with { Revision = 0 } : definition), reconcile ? "1" : "0", definition.Name }).ConfigureAwait(false);
        if ((long)result == -1)
            throw new JobException($"Schedule {definition.Name} changed. Reload it before saving.");
        if ((long)result == -2)
            throw new JobException($"Declared schedule {definition.Name} changed. Increase ConfigurationVersion to apply it.");
    }

    public async Task<ScheduledJobDefinition?> GetScheduleAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        cancellationToken.ThrowIfCancellationRequested();
        var value = await _db.HashGetAsync(ScheduleKey(name), "definition").ConfigureAwait(false);
        return value.IsNull ? null : JsonSerializer.Deserialize<ScheduledJobDefinition>((string)value!);
    }

    public Task UnscheduleAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        cancellationToken.ThrowIfCancellationRequested();
        return _db.ScriptEvaluateAsync("""
            redis.call('DEL', KEYS[1])
            redis.call('ZREM', KEYS[2], ARGV[1])
            return 1
            """, [ScheduleKey(name), SchedulesKey], [name]);
    }

    public async Task<IReadOnlyList<ScheduledJobDefinition>> GetSchedulesAsync(ScheduleQuery? query = null, CancellationToken cancellationToken = default)
    {
        query ??= new ScheduleQuery();
        query.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var result = await _db.ScriptEvaluateAsync("""
            local names = redis.call('ZRANGEBYLEX', KEYS[1], ARGV[1], '+', 'LIMIT', 0, ARGV[2])
            local definitions = {}
            for _, name in ipairs(names) do
                local definition = redis.call('HGET', ARGV[3] .. name, 'definition')
                if definition then table.insert(definitions, definition) end
            end
            return definitions
            """, [SchedulesKey], new RedisValue[] { query.AfterName is null ? "-" : "(" + query.AfterName, query.Limit, $"{_prefix}schedule:" }).ConfigureAwait(false);
        return ((RedisValue[]?)result ?? []).Select(value => JsonSerializer.Deserialize<ScheduledJobDefinition>((string)value!)!).ToArray();
    }

    private RedisKey ScheduleKey(string name) => $"{_prefix}schedule:{name}";
    private RedisKey SchedulesKey => $"{_prefix}schedules";
}

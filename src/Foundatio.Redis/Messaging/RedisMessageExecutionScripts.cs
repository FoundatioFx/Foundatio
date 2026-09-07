using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace Foundatio.Messaging;

// A job hash, its cancellation flag, and its indexes change in one server operation. Index scores
// remain creation times for pagination; a separate index records actual server expiration deadlines.
internal static class RedisMessageExecutionScripts
{
    public const string IncrementCounter = """
        local value = redis.call('HINCRBY', KEYS[1], ARGV[1], ARGV[2])
        redis.call('PEXPIRE', KEYS[1], ARGV[3])
        return value
        """;

    public const string Mutate = """
        local op, prefix, id = ARGV[1], ARGV[2], ARGV[3]
        local key, ttl, expected, now = KEYS[1], tonumber(ARGV[4]), ARGV[5], ARGV[6]
        local clock = redis.call('TIME')
        local serverNow = tonumber(clock[1]) * 1000 + math.floor(tonumber(clock[2]) / 1000)
        local function queueKey(queue) return prefix .. ':queues:' .. queue end
        local function statusKey(queue, status) return queueKey(queue) .. ':status:' .. status end
        local function terminal(status) return status == '2' or status == '3' or status == '4' end
        local function removeIndexes(queue, job)
            redis.call('ZREM', queueKey(queue), job)
            redis.call('ZREM', queueKey(queue) .. ':expires', job)
            for status = 0, 6 do redis.call('ZREM', statusKey(queue, status), job) end
        end
        local function cleanup(queue)
            local expires = queueKey(queue) .. ':expires'
            local members = redis.call('ZRANGEBYSCORE', expires, '-inf', serverNow, 'LIMIT', 0, 128)
            for _, job in ipairs(members) do
                local remaining = redis.call('PTTL', prefix .. ':' .. job)
                if remaining == -2 then
                    removeIndexes(queue, job)
                else
                    redis.call('ZADD', expires, remaining == -1 and 9007199254740991 or serverNow + math.max(1, remaining), job)
                end
            end
            return #members
        end
        local function touchIndex(index, score, retention)
            local existed = redis.call('EXISTS', index) == 1
            redis.call('ZADD', index, score, id)
            if retention < 0 then
                redis.call('PERSIST', index)
            else
                local remaining = redis.call('PTTL', index)
                if not existed or (remaining >= 0 and remaining < retention) then
                    redis.call('PEXPIRE', index, retention)
                end
            end
        end
        local function refresh(queue, status)
            local retention = ttl
            if retention == -2 then retention = tonumber(redis.call('HGET', key, 'RetentionMs') or '-1') end
            redis.call('HSET', key, 'RetentionMs', retention)
            if retention < 0 then
                redis.call('PERSIST', key)
                redis.call('PERSIST', key .. ':cancel')
            else
                redis.call('PEXPIRE', key, retention)
                redis.call('PEXPIRE', key .. ':cancel', retention)
            end
            local created = tonumber(redis.call('HGET', key, 'CreatedUtc') or '0')
            touchIndex(queueKey(queue), created, retention)
            touchIndex(statusKey(queue, status), created, retention)
            touchIndex(queueKey(queue) .. ':expires', retention < 0 and 9007199254740991 or serverNow + retention, retention)
            cleanup(queue)
        end
        if op == 'clean' then return cleanup(id) end
        if op == 'prune' then
            if redis.call('EXISTS', key) == 0 then removeIndexes(ARGV[7], id) end
            return 1
        end
        local oldQueue = redis.call('HGET', key, 'QueueName')
        local oldStatus = redis.call('HGET', key, 'Status')
        if op == 'remove' then
            if oldQueue then removeIndexes(oldQueue, id) end
            redis.call('DEL', key, key .. ':cancel')
            return 1
        end
        if op == 'set' then
            if oldQueue then removeIndexes(oldQueue, id) end
            redis.call('DEL', key, key .. ':cancel')
            for i = 7, #ARGV, 2 do redis.call('HSET', key, ARGV[i], ARGV[i + 1]) end
            refresh(redis.call('HGET', key, 'QueueName'), redis.call('HGET', key, 'Status'))
            return 1
        end
        if not oldQueue or terminal(oldStatus) then return 0 end
        local attempt = tonumber(redis.call('HGET', key, 'Attempt') or '0')
        if expected ~= '' and tonumber(expected) ~= attempt then return 0 end
        if op == 'cancel' then
            local remaining = redis.call('PTTL', key)
            redis.call('SET', key .. ':cancel', '1')
            if remaining >= 0 then redis.call('PEXPIRE', key .. ':cancel', math.max(1, remaining)) end
            return 1
        end
        if (op == 'progress' or op == 'heartbeat') and oldStatus ~= '1' then return 0 end
        if op == 'status' then
            local status, nextAttempt = oldStatus, nil
            for i = 7, #ARGV, 2 do
                if ARGV[i] == 'Status' then status = ARGV[i + 1] end
                if ARGV[i] == 'Attempt' then nextAttempt = tonumber(ARGV[i + 1]) end
            end
            if nextAttempt and nextAttempt < attempt then return 0 end
            if status == '1' and nextAttempt == attempt and (oldStatus == '1' or oldStatus == '5') then return 0 end
            if status == '6' and oldStatus ~= '0' then return 0 end
            if status ~= oldStatus then redis.call('ZREM', statusKey(oldQueue, oldStatus), id) end
            if not terminal(status) then redis.call('HDEL', key, 'CompletedUtc') end
            if status == '1' then redis.call('HDEL', key, 'ErrorMessage') end
        end
        for i = 7, #ARGV, 2 do redis.call('HSET', key, ARGV[i], ARGV[i + 1]) end
        redis.call('HSET', key, 'LastUpdatedUtc', now)
        refresh(oldQueue, redis.call('HGET', key, 'Status'))
        return 1
        """;
}

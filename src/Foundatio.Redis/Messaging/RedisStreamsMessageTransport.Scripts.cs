namespace Foundatio.Messaging;

public sealed partial class RedisStreamsMessageTransport
{
    private const string TopicRetentionFunctions = """
        local function hex(value)
            return (string.gsub(value, '.', function(c) return string.format('%02X', string.byte(c)) end))
        end
        local function cleanupSubscriptions(stream, now)
            local leases = stream .. ':subscriptions'
            for _, group in ipairs(redis.call('ZRANGEBYSCORE', leases, '-inf', now, 'LIMIT', 0, 100)) do
                if redis.call('EXISTS', stream) == 1 then redis.call('XGROUP', 'DESTROY', stream, group) end
                redis.call('DEL', stream .. ':lock:' .. hex(group), stream .. ':meta:' .. hex(group), stream .. ':dead:' .. hex(group))
                redis.call('ZREM', leases, group)
            end
        end
        local function less(a, b)
            local am, as = string.match(a, '^(%d+)%-(%d+)$')
            local bm, bs = string.match(b, '^(%d+)%-(%d+)$')
            return tonumber(am) < tonumber(bm) or (tonumber(am) == tonumber(bm) and tonumber(as) < tonumber(bs))
        end
        local function trimTopic(stream, now, force)
            local cadence = stream .. ':trim'
            if not force and redis.call('EXISTS', cadence) == 1 then return end
            redis.call('SET', cadence, '1', 'PX', 1000)
            cleanupSubscriptions(stream, now)
            if redis.call('EXISTS', stream) == 0 then return end
            local groups = redis.call('XINFO', 'GROUPS', stream)
            if #groups == 0 then redis.call('XTRIM', stream, 'MAXLEN', 0) return end
            local boundary, keep
            for _, group in ipairs(groups) do
                local name, last
                for index = 1, #group, 2 do
                    if group[index] == 'name' then name = group[index + 1] end
                    if group[index] == 'last-delivered-id' then last = group[index + 1] end
                end
                local pending = redis.call('XPENDING', stream, name, '-', '+', 1)
                local candidate = #pending > 0 and pending[1][1] or last
                local preserve = #pending > 0
                if not boundary or less(candidate, boundary) then
                    boundary, keep = candidate, preserve
                elseif candidate == boundary and preserve then keep = true end
            end
            redis.call('XTRIM', stream, 'MINID', boundary)
            if not keep then redis.call('XDEL', stream, boundary) end
        end
        """;

    private const string SendScript = TopicRetentionFunctions + """

        if ARGV[1] == '1' then trimTopic(KEYS[1], ARGV[3], redis.call('XLEN', KEYS[1]) >= tonumber(ARGV[2])) end
        if redis.call('XLEN', KEYS[1]) >= tonumber(ARGV[2]) then
            return redis.error_reply('The destination has reached its pending-message capacity.')
        end
        local id = redis.call('XADD', KEYS[1], '*', unpack(ARGV, 4))
        if ARGV[1] == '1' then trimTopic(KEYS[1], ARGV[3]) end
        return id
        """;

    private const string ReplayScript = TopicRetentionFunctions + """

        local entries = redis.call('XRANGE', KEYS[1], ARGV[1], ARGV[1], 'COUNT', 1)
        if #entries == 0 then return 0 end
        if ARGV[2] == '1' then trimTopic(KEYS[2], ARGV[4], redis.call('XLEN', KEYS[2]) >= tonumber(ARGV[3])) end
        if redis.call('XLEN', KEYS[2]) >= tonumber(ARGV[3]) then return redis.error_reply('Replay destination is full.') end
        local fields = entries[1][2]
        for index = 1, #fields, 2 do
            if fields[index] == 'h' then
                local headers = cjson.decode(fields[index + 1])
                for key, _ in pairs(headers) do
                    local normalized = string.lower(key)
                    if normalized == 'message.attempts' or normalized == 'message.expiration' or string.sub(normalized, 1, 20) == 'message.dead_letter.' then headers[key] = nil end
                end
                fields[index + 1] = cjson.encode(headers)
            end
        end
        redis.call('XADD', KEYS[2], '*', unpack(fields))
        if ARGV[2] == '1' then trimTopic(KEYS[2], ARGV[4]) end
        redis.call('XDEL', KEYS[1], ARGV[1])
        return 1
        """;

    private const string ReceiveScript = TopicRetentionFunctions + """

        cleanupSubscriptions(KEYS[1], ARGV[3])
        local result = {}
        local now, visibility, maximum = tonumber(ARGV[3]), tonumber(ARGV[4]), tonumber(ARGV[5])
        local function track(entry, deliveries)
            local token = ARGV[6] .. ':' .. entry[1]
            redis.call('HSET', KEYS[3], entry[1], token .. '|' .. deliveries)
            redis.call('ZADD', KEYS[2], now + visibility, entry[1])
            table.insert(result, {entry[1], entry[2], deliveries, token})
        end
        local due = redis.call('ZRANGEBYSCORE', KEYS[2], '-inf', now, 'LIMIT', 0, maximum)
        for _, id in ipairs(due) do
            local claimed = redis.call('XCLAIM', KEYS[1], ARGV[1], ARGV[2], 0, id)
            if #claimed > 0 then
                local meta = redis.call('HGET', KEYS[3], id) or ''
                local deliveries = tonumber(string.match(meta, '|(%d+)$') or '0') + 1
                track(claimed[1], deliveries)
            else
                redis.call('ZREM', KEYS[2], id)
                redis.call('HDEL', KEYS[3], id)
            end
        end
        if #result < maximum then
            local cursor = redis.call('HGET', KEYS[3], '@orphan-cursor') or '-'
            local pending = redis.call('XPENDING', KEYS[1], ARGV[1], cursor, '+', 100)
            local visited = 0
            for _, item in ipairs(pending) do
                if #result >= maximum then break end
                visited = visited + 1
                cursor = '(' .. item[1]
                if tonumber(item[3]) >= tonumber(ARGV[7]) and
                    (redis.call('HEXISTS', KEYS[3], item[1]) == 0 or not redis.call('ZSCORE', KEYS[2], item[1])) then
                    local claimed = redis.call('XCLAIM', KEYS[1], ARGV[1], ARGV[2], 0, item[1])
                    if #claimed > 0 then track(claimed[1], tonumber(item[4]) + 1) end
                end
            end
            if visited == #pending and #pending < 100 then cursor = '-' end
            redis.call('HSET', KEYS[3], '@orphan-cursor', cursor)
        end
        if #result < maximum then
            local fresh = redis.call('XREADGROUP', 'GROUP', ARGV[1], ARGV[2], 'COUNT', maximum - #result, 'STREAMS', KEYS[1], '>')
            if fresh then
                for _, entry in ipairs(fresh[1][2]) do track(entry, 1) end
            end
        end
        return result
        """;

    private const string SettleScript = TopicRetentionFunctions + """

        local meta = redis.call('HGET', KEYS[3], ARGV[2])
        local expires = tonumber(redis.call('ZSCORE', KEYS[2], ARGV[2]) or '0')
        if not meta or string.match(meta, '^([^|]*)') ~= ARGV[3] or expires <= tonumber(ARGV[4]) then return 0 end
        if #redis.call('XPENDING', KEYS[1], ARGV[1], ARGV[2], ARGV[2], 1) == 0 then return 0 end
        if ARGV[5] == 'renew' then
            redis.call('ZADD', KEYS[2], ARGV[6], ARGV[2])
            return 1
        end
        if ARGV[5] == 'abandon' then
            redis.call('ZADD', KEYS[2], ARGV[6], ARGV[2])
            redis.call('HSET', KEYS[3], ARGV[2], '|' .. (string.match(meta, '|(%d+)$') or '1'))
            return 1
        end
        if ARGV[5] == 'deadletter' then
            if redis.call('XLEN', KEYS[4]) >= tonumber(ARGV[8]) then
                return redis.error_reply('The dead-letter destination has reached its capacity.')
            end
            redis.call('XADD', KEYS[4], '*', unpack(ARGV, 9))
        end
        redis.call('XACK', KEYS[1], ARGV[1], ARGV[2])
        if ARGV[7] == '1' then redis.call('XDEL', KEYS[1], ARGV[2]) end
        redis.call('ZREM', KEYS[2], ARGV[2])
        redis.call('HDEL', KEYS[3], ARGV[2])
        if ARGV[7] == '0' then trimTopic(KEYS[1], ARGV[4]) end
        return 1
        """;
}

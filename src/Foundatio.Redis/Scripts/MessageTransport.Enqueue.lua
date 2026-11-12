-- KEYS: stream, delayed, delayed-data, groups, held, then one deduplication key per message that has one
-- ARGV: deduplication window ms, key ttl ms (0 = none), create ('1' creates a missing source, '0' skips it), consumer group,
--       then per message: delay ms, has deduplication key, delayed id, has group, group key, encoded fields
-- Returns the number of messages added (-1 when the source does not exist and was not created), the number of groups
-- released because their outstanding message was lost, and the number of messages in the source
local k = { stream = KEYS[1], delayed = KEYS[2], delayedData = KEYS[3], groups = KEYS[4], held = KEYS[5] }
local window = tonumber(ARGV[1])
local keyTtl = tonumber(ARGV[2])
local now = nowMs()

if ARGV[3] ~= '1' and redis.call('EXISTS', k.stream) == 0 then
    return { -1, 0, 0 }
end
local released = ensureStream(k, ARGV[4], now)

local nextKey = 6
local added = 0

for i = 5, #ARGV, 6 do
    local accepted = true
    if ARGV[i + 1] == '1' then
        accepted = redis.call('SET', KEYS[nextKey], '1', 'NX', 'PX', window)
        nextKey = nextKey + 1
    end

    if accepted then
        local delay = tonumber(ARGV[i])
        local group = nil
        if ARGV[i + 3] == '1' then
            group = ARGV[i + 4]
        end
        send(k, now, delay > 0 and now + delay or 0, ARGV[i + 2], ARGV[i + 5], group)
        added = added + 1
    end
end

if keyTtl > 0 then
    for key = 1, 5 do
        if redis.call('PTTL', KEYS[key]) == -1 then
            redis.call('PEXPIRE', KEYS[key], keyTtl)
        end
    end
end

return { added, released, redis.call('XLEN', k.stream) + redis.call('ZCARD', k.delayed) + redis.call('HLEN', k.held) }

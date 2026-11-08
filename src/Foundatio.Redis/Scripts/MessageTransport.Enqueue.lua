-- KEYS: stream, delayed, delayed-data, then one deduplication key per message that has one
-- ARGV: deduplication window ms, key ttl ms (0 = none), then per message: delay ms, has deduplication key, delayed id, encoded fields
local stream, delayed, delayedData = KEYS[1], KEYS[2], KEYS[3]
local window = tonumber(ARGV[1])
local keyTtl = tonumber(ARGV[2])
local now = nowMs()
local nextKey = 4
local added = 0

for i = 3, #ARGV, 4 do
    local accepted = true
    if ARGV[i + 1] == '1' then
        accepted = redis.call('SET', KEYS[nextKey], '1', 'NX', 'PX', window)
        nextKey = nextKey + 1
    end

    if accepted then
        local delay = tonumber(ARGV[i])
        if delay > 0 then
            redis.call('HSET', delayedData, ARGV[i + 2], ARGV[i + 3])
            redis.call('ZADD', delayed, now + delay, ARGV[i + 2])
        else
            redis.call('XADD', stream, '*', unpack(decodeFields(ARGV[i + 3])))
        end
        added = added + 1
    end
end

if keyTtl > 0 then
    for k = 1, 3 do
        if redis.call('PTTL', KEYS[k]) == -1 then
            redis.call('PEXPIRE', KEYS[k], keyTtl)
        end
    end
end

return added

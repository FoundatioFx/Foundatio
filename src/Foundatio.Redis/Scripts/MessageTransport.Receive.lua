-- KEYS: stream, leases, delayed, delayed-data
-- ARGV: group, consumer, max messages, lease ms, max delayed messages to promote
local stream, leases, delayed, delayedData = KEYS[1], KEYS[2], KEYS[3], KEYS[4]
local group, consumer = ARGV[1], ARGV[2]
local maxMessages, lease = tonumber(ARGV[3]), tonumber(ARGV[4])
local now = nowMs()

for _, id in ipairs(redis.call('ZRANGEBYSCORE', delayed, '-inf', now, 'LIMIT', 0, tonumber(ARGV[5]))) do
    local fields = redis.call('HGET', delayedData, id)
    if fields then
        redis.call('XADD', stream, '*', unpack(decodeFields(fields)))
        redis.call('HDEL', delayedData, id)
    end
    redis.call('ZREM', delayed, id)
end

if redis.call('EXISTS', stream) == 0 then
    redis.call('XGROUP', 'CREATE', stream, group, '0', 'MKSTREAM')
end

local messages = {}
for _, id in ipairs(redis.call('ZRANGEBYSCORE', leases, '-inf', now, 'LIMIT', 0, maxMessages)) do
    local claimed = redis.pcall('XCLAIM', stream, group, consumer, 0, id)
    if isError(claimed) or #claimed == 0 or type(claimed[1]) ~= 'table' or not claimed[1][2] then
        redis.call('ZREM', leases, id)
    else
        redis.call('ZADD', leases, now + lease, id)
        local pending = redis.call('XPENDING', stream, group, id, id, 1)
        messages[#messages + 1] = { id, claimed[1][2], pending[1][4] }
    end
end

local remaining = maxMessages - #messages
if remaining > 0 then
    local read = redis.pcall('XREADGROUP', 'GROUP', group, consumer, 'COUNT', remaining, 'STREAMS', stream, '>')
    if isError(read) then
        if not string.find(read.err, 'NOGROUP', 1, true) then
            return redis.error_reply(read.err)
        end
        redis.call('XGROUP', 'CREATE', stream, group, '0', 'MKSTREAM')
        read = redis.call('XREADGROUP', 'GROUP', group, consumer, 'COUNT', remaining, 'STREAMS', stream, '>')
    end

    if read then
        for _, entry in ipairs(read[1][2]) do
            redis.call('ZADD', leases, now + lease, entry[1])
            messages[#messages + 1] = { entry[1], entry[2], 1 }
        end
    end
end

local wait = -1
if #messages == 0 then
    local nextAt
    local nextDelayed = redis.call('ZRANGE', delayed, 0, 0, 'WITHSCORES')
    if nextDelayed[2] then
        nextAt = tonumber(nextDelayed[2])
    end
    local nextLease = redis.call('ZRANGE', leases, 0, 0, 'WITHSCORES')
    if nextLease[2] and (not nextAt or tonumber(nextLease[2]) < nextAt) then
        nextAt = tonumber(nextLease[2])
    end
    if nextAt then
        wait = math.max(0, nextAt - now)
    end
end

return { messages, wait }

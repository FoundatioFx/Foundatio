-- KEYS: stream, leases, delayed, delayed-data, groups, held
-- ARGV: group, consumer, max messages, lease ms, max delayed messages to promote, max bytes, max message bytes
-- Reads stop once about max bytes were read, always returning at least one message when one is available.
-- Returns the messages, the ms until the next delayed or leased message is due (-1 when there is none or messages were
-- returned), and the number of groups released because their outstanding message was lost
local stream, leases, delayed = KEYS[1], KEYS[2], KEYS[3]
local k = { stream = stream, delayed = delayed, delayedData = KEYS[4], groups = KEYS[5], held = KEYS[6] }
local group, consumer = ARGV[1], ARGV[2]
local maxMessages, lease = tonumber(ARGV[3]), tonumber(ARGV[4])
local maxBytes, maxMessageBytes = tonumber(ARGV[6]), tonumber(ARGV[7])
local now = nowMs()

local released = ensureStream(k, group, now)
released = released + promote(k, now, tonumber(ARGV[5]), maxBytes)

local messages = {}
local bytes = 0
for _, id in ipairs(redis.call('ZRANGEBYSCORE', leases, '-inf', now, 'LIMIT', 0, maxMessages)) do
    if bytes >= maxBytes then
        break
    end
    local claimed = redis.pcall('XCLAIM', stream, group, consumer, 0, id)
    if isError(claimed) or #claimed == 0 or type(claimed[1]) ~= 'table' or not claimed[1][2] then
        redis.call('ZREM', leases, id)
    else
        redis.call('ZADD', leases, now + lease, id)
        local pending = redis.call('XPENDING', stream, group, id, id, 1)
        messages[#messages + 1] = { id, claimed[1][2], pending[1][4] }
        bytes = bytes + fieldsSize(claimed[1][2])
    end
end

local function readGroup(count)
    local read = redis.pcall('XREADGROUP', 'GROUP', group, consumer, 'COUNT', count, 'STREAMS', stream, '>')
    if isError(read) then
        if not string.find(read.err, 'NOGROUP', 1, true) then
            return read
        end
        redis.call('XGROUP', 'CREATE', stream, group, '0', 'MKSTREAM')
        read = redis.call('XREADGROUP', 'GROUP', group, consumer, 'COUNT', count, 'STREAMS', stream, '>')
    end
    return read
end

-- Reads as many messages at once as fit in the remaining bytes at the largest message size, and one at a time once
-- that is none.
while #messages < maxMessages and bytes < maxBytes do
    local count = math.min(maxMessages - #messages, math.max(1, math.floor((maxBytes - bytes) / maxMessageBytes)))
    local read = readGroup(count)
    if isError(read) then
        return redis.error_reply(read.err)
    end
    if not read then
        break
    end

    local entries = read[1][2]
    for _, entry in ipairs(entries) do
        redis.call('ZADD', leases, now + lease, entry[1])
        messages[#messages + 1] = { entry[1], entry[2], 1 }
        bytes = bytes + fieldsSize(entry[2])
    end
    if #entries < count then
        break
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

return { messages, wait, released }

-- KEYS: stream, leases, delayed, delayed-data, groups, held, retries, idle
-- ARGV: group, consumer, max messages, lease ms, max delayed messages to promote, max bytes, max message bytes,
--       dead letters to keep and dead-letter retention ms (0 for sources that are not dead-letter queues),
--       register ('0' never registers the consumer as idle, '1' registers it when the receive drains the source, '2' does
--       too but leaves it be when it takes nothing, as it is still registered), registered ('1' when the consumer may
--       still be registered as idle)
-- Reads stop once about max bytes were read, always returning at least one message when one is available. The source
-- is drained when the stream had fewer unread messages than the receive could take.
-- Returns the messages, the ms until the next delayed or leased message is due (-1 when there is none or the source was
-- not drained), the number of groups released because their outstanding message was lost, 1 when it was drained, and 1
-- when the source's stream or consumer group was missing (Redis lost its keys) and was created again
local stream, leases, delayed, idle = KEYS[1], KEYS[2], KEYS[3], KEYS[8]
local k = { stream = stream, delayed = delayed, delayedData = KEYS[4], groups = KEYS[5], held = KEYS[6] }
local group, consumer = ARGV[1], ARGV[2]
local maxMessages, lease = tonumber(ARGV[3]), tonumber(ARGV[4])
local maxBytes, maxMessageBytes = tonumber(ARGV[6]), tonumber(ARGV[7])
local now = nowMs()

-- Retries are kept in the retries set only while they wait out their delay, so their source's stats can tell them
-- from messages that are in flight; once due they are leases like any other.
redis.call('ZREMRANGEBYSCORE', KEYS[7], '-inf', now)
local recreated = redis.call('EXISTS', stream) == 0
local released = ensureStream(k, group, now)
released = released + promote(k, now, tonumber(ARGV[5]), maxBytes)
trimDeadLetters(stream, tonumber(ARGV[8]), tonumber(ARGV[9]))

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
        recreated = true
        read = redis.call('XREADGROUP', 'GROUP', group, consumer, 'COUNT', count, 'STREAMS', stream, '>')
    end
    return read
end

-- Reads as many messages at once as fit in the remaining bytes at the largest message size. When that is fewer than
-- wanted (the default largest size is over the budget) and the messages read so far are small, the entries after the
-- last one read are sized first without claiming them, so one read takes all that fit instead of one read per message.
-- Sizing copies the entries once more, which costs more than the reads it saves once messages are a few KiB.
local peekBelow = 4096
local drained = false
local lastId, largest = nil, 0
while #messages < maxMessages and bytes < maxBytes do
    local want = maxMessages - #messages
    local count = math.min(want, math.max(1, math.floor((maxBytes - bytes) / maxMessageBytes)))
    if count < want and lastId and largest <= peekBelow then
        local fit, sized = 0, bytes
        for _, entry in ipairs(redis.call('XRANGE', stream, '(' .. lastId, '+', 'COUNT', want)) do
            if sized >= maxBytes then
                break
            end
            fit = fit + 1
            sized = sized + fieldsSize(entry[2])
        end
        count = math.max(fit, 1)
    end
    local read = readGroup(count)
    if isError(read) then
        return redis.error_reply(read.err)
    end
    if not read then
        drained = true
        break
    end

    local entries = read[1][2]
    for _, entry in ipairs(entries) do
        redis.call('ZADD', leases, now + lease, entry[1])
        messages[#messages + 1] = { entry[1], entry[2], 1 }
        local size = fieldsSize(entry[2])
        bytes = bytes + size
        largest = math.max(largest, size)
        lastId = entry[1]
    end
    if #entries < count then
        drained = true
        break
    end
end

local wait = -1
if drained then
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

-- A receiver that drained the source, and still has room, waits to be woken; one with no room is busy.
if drained and ARGV[10] ~= '0' then
    if #messages > 0 then
        redis.call('ZADD', idle, now, consumer)
    elseif ARGV[10] == '1' then
        redis.call('ZADD', idle, 'NX', now, consumer)
    end
elseif not drained and ARGV[11] == '1' then
    redis.call('ZREM', idle, consumer)
end

return { messages, wait, released, drained and 1 or 0, recreated and 1 or 0 }

local function nowMs()
    local time = redis.call('TIME')
    return tonumber(time[1]) * 1000 + math.floor(tonumber(time[2]) / 1000)
end

local function decodeFields(encoded)
    local fields = {}
    local position = 1
    local length = #encoded
    while position <= length do
        local size = struct.unpack('>I4', encoded, position)
        position = position + 4
        fields[#fields + 1] = string.sub(encoded, position, position + size - 1)
        position = position + size
    end
    return fields
end

local function isError(reply)
    return type(reply) == 'table' and reply.err ~= nil
end

-- Ordered groups: at most one message of a group is outstanding (in the stream, or delayed) per source; later ones
-- are held until it is completed or dead-lettered. The groups hash maps 'a:<group>' to the outstanding message (a
-- stream id, or 'd:<delayed id>'), 'd:<delayed id>' back to its group, 'h:<group>' and 't:<group>' to the group's
-- first and last held sequence, 'n:<sequence>' to the next held sequence of that group and 'w:<sequence>' to a held
-- message's due time. The held hash maps each sequence to the message's encoded fields.

-- Adds a message to the stream, or to the delayed set until it is due, and records it as its group's outstanding one.
local function release(k, now, due, delayedId, encoded, group)
    if due > now then
        redis.call('HSET', k.delayedData, delayedId, encoded)
        redis.call('ZADD', k.delayed, due, delayedId)
        if group then
            redis.call('HSET', k.groups, 'a:' .. group, 'd:' .. delayedId, 'd:' .. delayedId, group)
        end
        return
    end

    local id = redis.call('XADD', k.stream, '*', unpack(decodeFields(encoded)))
    if group then
        redis.call('HSET', k.groups, 'a:' .. group, id)
    end
end

local function hold(k, group, due, encoded)
    local sequence = redis.call('HINCRBY', k.groups, 'seq', 1)
    redis.call('HSET', k.held, sequence, encoded)
    if due > 0 then
        redis.call('HSET', k.groups, 'w:' .. sequence, due)
    end

    local tail = redis.call('HGET', k.groups, 't:' .. group)
    if tail then
        redis.call('HSET', k.groups, 'n:' .. tail, sequence)
    else
        redis.call('HSET', k.groups, 'h:' .. group, sequence)
    end
    redis.call('HSET', k.groups, 't:' .. group, sequence)
end

-- Sends a message, holding it while an earlier message of its group is outstanding. group is nil for ungrouped messages.
local function send(k, now, due, delayedId, encoded, group)
    if group and redis.call('HEXISTS', k.groups, 'a:' .. group) == 1 then
        hold(k, group, due, encoded)
    else
        release(k, now, due, delayedId, encoded, group)
    end
end

-- Called when a message left the source. If it was its group's outstanding message, releases the group's next held
-- message. Returns true when one was released.
local function advance(k, now, group, id)
    local outstanding = 'a:' .. group
    if redis.call('HGET', k.groups, outstanding) ~= id then
        return false
    end

    local head = redis.call('HGET', k.groups, 'h:' .. group)
    while head do
        local nextHead = redis.call('HGET', k.groups, 'n:' .. head)
        local due = tonumber(redis.call('HGET', k.groups, 'w:' .. head)) or 0
        local encoded = redis.call('HGET', k.held, head)
        redis.call('HDEL', k.groups, 'n:' .. head, 'w:' .. head)
        redis.call('HDEL', k.held, head)
        if nextHead then
            redis.call('HSET', k.groups, 'h:' .. group, nextHead)
        else
            redis.call('HDEL', k.groups, 'h:' .. group, 't:' .. group)
        end

        if encoded then
            release(k, now, due, 'h' .. head, encoded, group)
            return true
        end
        head = nextHead
    end

    redis.call('HDEL', k.groups, outstanding)
    return false
end

-- Moves due delayed messages into the stream; a promoted message stays its group's outstanding one.
local function promote(k, now, max)
    for _, id in ipairs(redis.call('ZRANGEBYSCORE', k.delayed, '-inf', now, 'LIMIT', 0, max)) do
        local encoded = redis.call('HGET', k.delayedData, id)
        if encoded then
            local entryId = redis.call('XADD', k.stream, '*', unpack(decodeFields(encoded)))
            local group = redis.call('HGET', k.groups, 'd:' .. id)
            if group then
                redis.call('HSET', k.groups, 'a:' .. group, entryId)
                redis.call('HDEL', k.groups, 'd:' .. id)
            end
            redis.call('HDEL', k.delayedData, id)
        end
        redis.call('ZREM', k.delayed, id)
    end
end

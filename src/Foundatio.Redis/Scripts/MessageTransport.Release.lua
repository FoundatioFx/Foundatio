-- KEYS: stream, leases, idle
-- ARGV: group, consumer, released consumer, wake channel prefix, publish command
-- Makes the consumer's messages available to other receivers at once, waking an idle receiver for each, and removes it.
-- Returns the number of messages released
local stream, leases, idle = KEYS[1], KEYS[2], KEYS[3]
local group, consumer, released = ARGV[1], ARGV[2], ARGV[3]
local count = 0

redis.call('ZREM', idle, consumer)
while true do
    local pending = redis.pcall('XPENDING', stream, group, '-', '+', 100, consumer)
    if isError(pending) or #pending == 0 then
        break
    end

    for _, entry in ipairs(pending) do
        redis.call('XCLAIM', stream, group, released, 0, entry[1], 'JUSTID')
        redis.call('ZADD', leases, 0, entry[1])
        count = count + 1
    end
end

redis.pcall('XGROUP', 'DELCONSUMER', stream, group, consumer)
wake(idle, count, ARGV[4], ARGV[5])
return count

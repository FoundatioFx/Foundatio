-- KEYS: stream, leases
-- ARGV: group, consumer, released consumer
local stream, leases = KEYS[1], KEYS[2]
local group, consumer, released = ARGV[1], ARGV[2], ARGV[3]
local count = 0

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
return count

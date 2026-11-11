-- KEYS: stream, leases, dead-letter stream, groups, held, delayed, delayed-data
-- ARGV: operation, group, consumer, entry id, expected delivery count, value (delay ms, lease ms or reason), released consumer,
--       reason field, has message group, message group
-- Returns 0 when the lease was lost, 1 when settled, 2 when settled and the next held message of its group was released
local stream, leases = KEYS[1], KEYS[2]
local operation, group, consumer, id = ARGV[1], ARGV[2], ARGV[3], ARGV[4]

local pending = redis.pcall('XPENDING', stream, group, id, id, 1)
if isError(pending) or #pending == 0 or pending[1][2] ~= consumer or pending[1][4] ~= tonumber(ARGV[5]) then
    return 0
end

if operation == 'renew' then
    redis.call('ZADD', leases, nowMs() + tonumber(ARGV[6]), id)
    return 1
end

if operation == 'abandon' then
    local availableAt = nowMs() + tonumber(ARGV[6])
    redis.call('XCLAIM', stream, group, ARGV[7], 0, id, 'JUSTID')
    redis.call('ZADD', leases, availableAt, id)
    return 1
end

if operation == 'deadletter' then
    local entry = redis.call('XRANGE', stream, id, id)
    if #entry > 0 then
        local fields = entry[1][2]
        local copy = {}
        for i = 1, #fields, 2 do
            if fields[i] ~= ARGV[8] then
                copy[#copy + 1] = fields[i]
                copy[#copy + 1] = fields[i + 1]
            end
        end
        copy[#copy + 1] = ARGV[8]
        copy[#copy + 1] = ARGV[6]
        redis.call('XADD', KEYS[3], '*', unpack(copy))
    end
end

redis.call('XACK', stream, group, id)
redis.call('XDEL', stream, id)
redis.call('ZREM', leases, id)

if ARGV[9] == '1' then
    local k = { stream = stream, groups = KEYS[4], held = KEYS[5], delayed = KEYS[6], delayedData = KEYS[7] }
    if advance(k, nowMs(), ARGV[10], id) then
        return 2
    end
end
return 1

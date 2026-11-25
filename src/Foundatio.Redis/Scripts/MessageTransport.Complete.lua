-- KEYS: stream, leases, groups, held, delayed, delayed-data, idle
-- ARGV: group, consumer, wake channel prefix, publish command, then per message: entry id, expected delivery count, has
--       message group, message group
-- Completes each message whose lease the consumer still holds, and wakes an idle receiver for each next message of a
-- group it releases.
-- Returns per message 0 when its lease was lost, 1 when completed, 2 when completed and the next held message of its
-- group was released
local stream, leases = KEYS[1], KEYS[2]
local group, consumer = ARGV[1], ARGV[2]

local results, ids, seen = {}, {}, {}
for i = 5, #ARGV, 4 do
    local id = ARGV[i]
    local pending = redis.pcall('XPENDING', stream, group, id, id, 1)
    if seen[id] or isError(pending) or #pending == 0 or pending[1][2] ~= consumer or pending[1][4] ~= tonumber(ARGV[i + 1]) then
        results[#results + 1] = 0
    else
        seen[id] = true
        results[#results + 1] = 1
        ids[#ids + 1] = id
    end
end

if #ids > 0 then
    redis.call('XACK', stream, group, unpack(ids))
    redis.call('XDEL', stream, unpack(ids))
    redis.call('ZREM', leases, unpack(ids))
end

local k = { stream = stream, groups = KEYS[3], held = KEYS[4], delayed = KEYS[5], delayedData = KEYS[6] }
local now
local n = 0
for i = 5, #ARGV, 4 do
    n = n + 1
    if results[n] == 1 and ARGV[i + 2] == '1' then
        now = now or nowMs()
        if advance(k, now, ARGV[i + 3], ARGV[i]) then
            wake(KEYS[7], 1, ARGV[3], ARGV[4])
            results[n] = 2
        end
    end
end

return results

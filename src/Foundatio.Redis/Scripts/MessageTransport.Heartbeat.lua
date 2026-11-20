-- KEYS: instances, then the instance subscription's stream, leases, delayed, delayed-data, groups, held, retries and idle keys
-- ARGV: instance name, timeout ms, key ttl ms, consumer group
-- Renews the instance subscription and the expiry of its keys, creating its stream when it is missing.
-- Returns 1 when the subscription was missing (it is new, or was removed as stale), 1 when its stream was missing, and
-- the number of groups released because their outstanding message was lost
local now = nowMs()
local added = redis.call('ZADD', KEYS[1], now + tonumber(ARGV[2]), ARGV[1])
local k = { stream = KEYS[2], delayed = KEYS[4], delayedData = KEYS[5], groups = KEYS[6], held = KEYS[7] }

local missing = 0
local released = 0
if redis.call('EXISTS', k.stream) == 0 then
    missing = 1
    redis.call('XGROUP', 'CREATE', k.stream, ARGV[4], '0', 'MKSTREAM')
    released = releaseLostGroups(k, now)
end

local ttl = tonumber(ARGV[3])
for i = 2, #KEYS do
    redis.call('PEXPIRE', KEYS[i], ttl)
end

return { added, missing, released }

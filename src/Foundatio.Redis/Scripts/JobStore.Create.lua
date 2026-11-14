-- KEYS: job, queued, index, name index, queued index, queued name index
-- ARGV: id, name, available at, index member, then field/value pairs
-- Returns 1 when created, 0 when a job with the id exists, -1 when an expired job still holds the id.
local id, name = ARGV[1], ARGV[2]
if redis.call('EXISTS', KEYS[1]) == 1 then
    if isExpired(redis.call('HGET', KEYS[1], 'expires')) then
        return -1
    end
    return 0
end

local fields = {}
for i = 5, #ARGV do
    fields[#fields + 1] = ARGV[i]
end
if #fields > 0 then
    redis.call('HSET', KEYS[1], unpack(fields))
end
redis.call('HSET', KEYS[1], 'id', id, 'name', name, 'status', QUEUED, 'attempt', 0, 'available', ARGV[3], 'updated', str(now), 'index', ARGV[4])
redis.call('ZADD', KEYS[2], ARGV[3], id)
for i = 3, 6 do
    redis.call('ZADD', KEYS[i], 0, ARGV[4])
end
return 1

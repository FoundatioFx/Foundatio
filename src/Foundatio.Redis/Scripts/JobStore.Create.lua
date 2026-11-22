-- KEYS: job, queued, index, name index, queued index, queued name index
-- ARGV: id, name, available at, '1' when available at is an offset from now, index member, then field/value pairs
-- Returns 1 when created, 0 when a job with the id exists, -1 when an expired job still holds the id. A job created
-- without a 'scheduled' field is scheduled for when it becomes available.
local id, name = ARGV[1], ARGV[2]
if redis.call('EXISTS', KEYS[1]) == 1 then
    if isExpired(redis.call('HGET', KEYS[1], 'expires')) then
        return -1
    end
    return 0
end

local available = tonumber(ARGV[3])
if ARGV[4] == '1' then
    available = now + available
end

local fields = {}
for i = 6, #ARGV do
    fields[#fields + 1] = ARGV[i]
end
if #fields > 0 then
    redis.call('HSET', KEYS[1], unpack(fields))
end
redis.call('HSET', KEYS[1], 'id', id, 'name', name, 'status', QUEUED, 'attempt', 0, 'available', str(available), 'updated', str(now), 'index', ARGV[5])
if redis.call('HEXISTS', KEYS[1], 'scheduled') == 0 then
    redis.call('HSET', KEYS[1], 'scheduled', str(available))
end
redis.call('ZADD', KEYS[2], available, id)
for i = 3, 6 do
    redis.call('ZADD', KEYS[i], 0, ARGV[5])
end
return 1

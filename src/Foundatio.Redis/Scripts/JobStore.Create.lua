-- KEYS: job
-- ARGV: prefix, id, name, available at, index member, then field/value pairs
local id, name = ARGV[2], ARGV[3]
removeExpired(100)

if redis.call('EXISTS', KEYS[1]) == 1 then
    if not isExpired(redis.call('HGET', KEYS[1], 'expires')) then
        return 0
    end
    removeJob(id)
end

local fields = {}
for i = 6, #ARGV do
    fields[#fields + 1] = ARGV[i]
end
redis.call('HSET', KEYS[1], unpack(fields))
redis.call('HSET', KEYS[1], 'id', id, 'name', name, 'status', QUEUED, 'attempt', 0, 'available', ARGV[4], 'updated', str(now), 'index', ARGV[5])
redis.call('ZADD', queuedKey(name), ARGV[4], id)
for _, index in ipairs(indexKeys(QUEUED, name)) do
    redis.call('ZADD', index, 0, ARGV[5])
end
return 1

-- KEYS: job
-- ARGV: prefix, id, retain us
local id = ARGV[2]
local job = redis.call('HMGET', KEYS[1], 'status', 'name', 'index', 'expires')
if not job[1] or isExpired(job[4]) or (job[1] ~= QUEUED and job[1] ~= RUNNING) then
    return 0
end

redis.call('HSET', KEYS[1], 'cancel', '1', 'updated', str(now))
if job[1] == QUEUED then
    local expiresAt = now + tonumber(ARGV[3])
    redis.call('HSET', KEYS[1], 'status', CANCELLED, 'completed', str(now), 'expires', str(expiresAt))
    redis.call('ZREM', queuedKey(job[2]), id)
    redis.call('ZADD', prefix .. 'expiring', expiresAt, id)
    moveIndexes(job[3], job[2], QUEUED, CANCELLED)
end
return 1

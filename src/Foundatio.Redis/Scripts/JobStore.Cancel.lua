-- KEYS: job, queued, expiring, queued index, queued name index, cancelled index, cancelled name index, holders,
--       waiting and pending (the name's concurrency keys, jobs waiting for one, and deduplication keys)
-- ARGV: id, retain us, name
-- Returns 1 when cancelled or flagged, 0 for a missing or finished job, -1 when the job's name is not the one given.
local id = ARGV[1]
local job = redis.call('HMGET', KEYS[1], 'status', 'name', 'index', 'expires', 'ckey', 'dkey', 'available')
if not job[1] or isExpired(job[4]) or (job[1] ~= QUEUED and job[1] ~= RUNNING) then
    return 0
end
if job[2] ~= ARGV[3] then
    return -1
end

redis.call('HSET', KEYS[1], 'cancel', '1', 'updated', str(now))
if job[1] == QUEUED then
    local expiresAt = now + tonumber(ARGV[2])
    redis.call('HSET', KEYS[1], 'status', CANCELLED, 'completed', str(now), 'expires', str(expiresAt))
    redis.call('ZREM', KEYS[2], id)
    releaseQueued(KEYS[8], KEYS[9], KEYS[2], KEYS[10], present(job[5]), present(job[6]), job[7], id)
    redis.call('ZADD', KEYS[3], expiresAt, id)
    redis.call('ZREM', KEYS[4], job[3])
    redis.call('ZREM', KEYS[5], job[3])
    redis.call('ZADD', KEYS[6], 0, job[3])
    redis.call('ZADD', KEYS[7], 0, job[3])
end
return 1

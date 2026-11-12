-- KEYS: job, running, queued, expiring, running index, running name index, target status index, target status name index
-- ARGV: id, claim token, status, retry delay us, refund attempt, retain us, has message, message, has error, error, name
local id, status = ARGV[1], ARGV[3]
local job = redis.call('HMGET', KEYS[1], 'status', 'token', 'name', 'index', 'attempt')
if job[1] ~= RUNNING or job[2] ~= ARGV[2] or job[3] ~= ARGV[11] then
    return 0
end

redis.call('HDEL', KEYS[1], 'token', 'lease', 'worker')
redis.call('HSET', KEYS[1], 'status', status, 'updated', str(now))
redis.call('ZREM', KEYS[2], id)

if status == QUEUED then
    local availableAt = now + tonumber(ARGV[4])
    redis.call('HSET', KEYS[1], 'available', str(availableAt))
    if ARGV[5] == '1' then
        redis.call('HSET', KEYS[1], 'attempt', math.max(0, tonumber(job[5]) - 1))
    end
    if ARGV[9] == '1' then
        redis.call('HSET', KEYS[1], 'error', ARGV[10])
    end
    redis.call('ZADD', KEYS[3], availableAt, id)
else
    local expiresAt = now + tonumber(ARGV[6])
    redis.call('HSET', KEYS[1], 'completed', str(now), 'expires', str(expiresAt))
    if ARGV[7] == '1' then
        redis.call('HSET', KEYS[1], 'message', ARGV[8])
    end
    if ARGV[9] == '1' then
        redis.call('HSET', KEYS[1], 'error', ARGV[10])
    elseif status == SUCCEEDED then
        redis.call('HDEL', KEYS[1], 'error')
    end
    if status == SUCCEEDED then
        redis.call('HDEL', KEYS[1], 'payload')
    end
    redis.call('ZADD', KEYS[4], expiresAt, id)
end

redis.call('ZREM', KEYS[5], job[4])
redis.call('ZREM', KEYS[6], job[4])
redis.call('ZADD', KEYS[7], 0, job[4])
redis.call('ZADD', KEYS[8], 0, job[4])
return 1

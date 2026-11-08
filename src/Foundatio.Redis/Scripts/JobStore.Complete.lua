-- KEYS: job
-- ARGV: prefix, id, claim token, status, retry delay us, refund attempt, retain us, has message, message, has error, error
local id, status = ARGV[2], ARGV[4]
local job = redis.call('HMGET', KEYS[1], 'status', 'token', 'name', 'index', 'attempt')
if job[1] ~= RUNNING or job[2] ~= ARGV[3] then
    return 0
end

local name = job[3]
redis.call('HDEL', KEYS[1], 'token', 'lease', 'worker')
redis.call('HSET', KEYS[1], 'status', status, 'updated', str(now))
redis.call('ZREM', runningKey(name), id)

if status == QUEUED then
    local availableAt = now + tonumber(ARGV[5])
    redis.call('HSET', KEYS[1], 'available', str(availableAt))
    if ARGV[6] == '1' then
        redis.call('HSET', KEYS[1], 'attempt', math.max(0, tonumber(job[5]) - 1))
    end
    if ARGV[10] == '1' then
        redis.call('HSET', KEYS[1], 'error', ARGV[11])
    end
    redis.call('ZADD', queuedKey(name), availableAt, id)
else
    local expiresAt = now + tonumber(ARGV[7])
    redis.call('HSET', KEYS[1], 'completed', str(now), 'expires', str(expiresAt))
    if ARGV[8] == '1' then
        redis.call('HSET', KEYS[1], 'message', ARGV[9])
    end
    if ARGV[10] == '1' then
        redis.call('HSET', KEYS[1], 'error', ARGV[11])
    elseif status == SUCCEEDED then
        redis.call('HDEL', KEYS[1], 'error')
    end
    redis.call('ZADD', prefix .. 'expiring', expiresAt, id)
end

moveIndexes(job[4], name, RUNNING, status)
return 1

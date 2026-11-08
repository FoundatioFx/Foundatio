-- ARGV: prefix, worker id, lease us, claim token, then the job names
local worker, lease, token = ARGV[2], tonumber(ARGV[3]), ARGV[4]
removeExpired(100)

for _ = 1, 10 do
    local bestId, bestDue, bestName, bestQueued
    for i = 5, #ARGV do
        local name = ARGV[i]
        local queued = redis.call('ZRANGEBYSCORE', queuedKey(name), '-inf', now, 'WITHSCORES', 'LIMIT', 0, 1)
        if queued[1] and (not bestDue or tonumber(queued[2]) < bestDue) then
            bestId, bestDue, bestName, bestQueued = queued[1], tonumber(queued[2]), name, true
        end
        local running = redis.call('ZRANGEBYSCORE', runningKey(name), '-inf', now, 'WITHSCORES', 'LIMIT', 0, 1)
        if running[1] and (not bestDue or tonumber(running[2]) < bestDue) then
            bestId, bestDue, bestName, bestQueued = running[1], tonumber(running[2]), name, false
        end
    end

    if not bestId then
        return false
    end

    local key = jobKey(bestId)
    local job = redis.call('HMGET', key, 'status', 'index', 'started')
    if not job[1] then
        redis.call('ZREM', queuedKey(bestName), bestId)
        redis.call('ZREM', runningKey(bestName), bestId)
    else
        local leaseExpires = now + lease
        redis.call('HINCRBY', key, 'attempt', 1)
        redis.call('HSET', key, 'status', RUNNING, 'token', token, 'worker', worker, 'lease', str(leaseExpires), 'updated', str(now))
        if not job[3] then
            redis.call('HSET', key, 'started', str(now))
        end
        if bestQueued then
            redis.call('ZREM', queuedKey(bestName), bestId)
            moveIndexes(job[2], bestName, job[1], RUNNING)
        end
        redis.call('ZADD', runningKey(bestName), leaseExpires, bestId)
        return redis.call('HGETALL', key)
    end
end

return false

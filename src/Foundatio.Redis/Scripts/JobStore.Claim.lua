-- KEYS: queued index, running index, then queued, running, queued name index and running name index for each
--       name, then each candidate's job
-- ARGV: worker id, lease us, claim token prefix, name count, the names, then id, name ordinal and '1' when queued
--       for each candidate, in the order to try them
-- Claims the first candidate that is still due and returns it, or false when other workers took them all.
local worker, lease, tokenPrefix, nameCount = ARGV[1], tonumber(ARGV[2]), ARGV[3], tonumber(ARGV[4])
local jobKeys = 2 + 4 * nameCount
local candidate = 0
for i = 5 + nameCount, #ARGV, 3 do
    candidate = candidate + 1
    local id, n, fromQueued = ARGV[i], tonumber(ARGV[i + 1]), ARGV[i + 2] == '1'
    local nameKeys = 2 + 4 * (n - 1)
    local queued, running = KEYS[nameKeys + 1], KEYS[nameKeys + 2]
    local key = KEYS[jobKeys + candidate]
    local source = fromQueued and queued or running
    local due = redis.call('ZSCORE', source, id)
    if due and tonumber(due) <= now then
        local job = redis.call('HMGET', key, 'status', 'index', 'started')
        if job[1] ~= (fromQueued and QUEUED or RUNNING) then
            redis.call('ZREM', source, id)
        else
            local leaseExpires = now + lease
            redis.call('HINCRBY', key, 'attempt', 1)
            redis.call('HSET', key, 'status', RUNNING, 'token', tokenPrefix .. ARGV[4 + n], 'worker', worker, 'lease', str(leaseExpires), 'updated', str(now))
            if not job[3] then
                redis.call('HSET', key, 'started', str(now))
            end
            if fromQueued then
                redis.call('ZREM', queued, id)
                redis.call('ZREM', KEYS[1], job[2])
                redis.call('ZREM', KEYS[nameKeys + 3], job[2])
                redis.call('ZADD', KEYS[2], 0, job[2])
                redis.call('ZADD', KEYS[nameKeys + 4], 0, job[2])
            end
            redis.call('ZADD', running, leaseExpires, id)
            return redis.call('HGETALL', key)
        end
    end
end

return false

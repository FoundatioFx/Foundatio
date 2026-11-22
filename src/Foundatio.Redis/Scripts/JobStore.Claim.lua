-- KEYS: queued index, running index, then queued, running, queued name index, running name index, holders (the
--       name's concurrency keys and the jobs holding them), waiting (the name's jobs waiting for a concurrency key)
--       and pending (the name's deduplication keys) for each name, then each candidate's job
-- ARGV: worker id, lease us, claim token prefix (24 characters), name count, most jobs to claim, the names, then id,
--       name ordinal and '1' when queued for each candidate, in the order to try them
-- Claims the candidates that are still due, in order, up to the most jobs to claim, and returns each claimed job's
-- fields; an empty list when other workers took them all. A queued candidate whose concurrency key another job holds
-- moves to the waiting set instead. Each claim token is the prefix, the claim's number in 8 hex digits, then the
-- job's name.
local worker, lease, tokenPrefix, nameCount, maxClaims = ARGV[1], tonumber(ARGV[2]), ARGV[3], tonumber(ARGV[4]), tonumber(ARGV[5])
local NAME_KEYS = 7
local jobKeys = 2 + NAME_KEYS * nameCount
local claimed = {}
local candidate = 0
for i = 6 + nameCount, #ARGV, 3 do
    if #claimed >= maxClaims then
        break
    end
    candidate = candidate + 1
    local id, n, fromQueued = ARGV[i], tonumber(ARGV[i + 1]), ARGV[i + 2] == '1'
    local nameKeys = 2 + NAME_KEYS * (n - 1)
    local queued, running, holders = KEYS[nameKeys + 1], KEYS[nameKeys + 2], KEYS[nameKeys + 5]
    local key = KEYS[jobKeys + candidate]
    local source = fromQueued and queued or running
    local due = redis.call('ZSCORE', source, id)
    if due and tonumber(due) <= now then
        local job = redis.call('HMGET', key, 'ckey', 'dkey', 'status', 'index', 'started')
        local concurrencyKey, deduplicationKey = present(job[1]), present(job[2])
        local holder = concurrencyKey and redis.call('HGET', holders, concurrencyKey)
        if job[3] ~= (fromQueued and QUEUED or RUNNING) then
            redis.call('ZREM', source, id)
        elseif fromQueued and holder and holder ~= id then
            redis.call('ZREM', queued, id)
            redis.call('ZADD', KEYS[nameKeys + 6], 0, waitingMember(concurrencyKey, redis.call('HGET', key, 'available'), id))
        else
            local leaseExpires = now + lease
            local token = tokenPrefix .. string.format('%08x', #claimed) .. ARGV[5 + n]
            redis.call('HINCRBY', key, 'attempt', 1)
            redis.call('HSET', key, 'status', RUNNING, 'token', token, 'worker', worker, 'lease', str(leaseExpires), 'updated', str(now))
            if not job[5] then
                redis.call('HSET', key, 'started', str(now))
            end
            if fromQueued then
                redis.call('ZREM', queued, id)
                redis.call('ZREM', KEYS[1], job[4])
                redis.call('ZREM', KEYS[nameKeys + 3], job[4])
                redis.call('ZADD', KEYS[2], 0, job[4])
                redis.call('ZADD', KEYS[nameKeys + 4], 0, job[4])
                if deduplicationKey and redis.call('HGET', KEYS[nameKeys + 7], deduplicationKey) == id then
                    redis.call('HDEL', KEYS[nameKeys + 7], deduplicationKey)
                end
            end
            if concurrencyKey and not holder then
                redis.call('HSET', holders, concurrencyKey, id)
            end
            redis.call('ZADD', running, leaseExpires, id)
            claimed[#claimed + 1] = redis.call('HGETALL', key)
        end
    end
end

return claimed

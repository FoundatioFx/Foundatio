-- KEYS: queued index, running index, then queued, running, queued name index and running name index for each
--       name, then each candidate's job
-- ARGV: worker id, lease us, claim token prefix (24 characters), name count, most jobs to claim, the names, then id,
--       name ordinal and '1' when queued for each candidate, in the order to try them
-- Claims the candidates that are still due, in order, up to the most jobs to claim, and returns each claimed job's
-- fields; an empty list when other workers took them all. Each claim token is the prefix, the claim's number in 8 hex
-- digits, then the job's name.
local worker, lease, tokenPrefix, nameCount, maxClaims = ARGV[1], tonumber(ARGV[2]), ARGV[3], tonumber(ARGV[4]), tonumber(ARGV[5])
local jobKeys = 2 + 4 * nameCount
local claimed = {}
local candidate = 0
for i = 6 + nameCount, #ARGV, 3 do
    if #claimed >= maxClaims then
        break
    end
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
            local token = tokenPrefix .. string.format('%08x', #claimed) .. ARGV[5 + n]
            redis.call('HINCRBY', key, 'attempt', 1)
            redis.call('HSET', key, 'status', RUNNING, 'token', token, 'worker', worker, 'lease', str(leaseExpires), 'updated', str(now))
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
            claimed[#claimed + 1] = redis.call('HGETALL', key)
        end
    end
end

return claimed

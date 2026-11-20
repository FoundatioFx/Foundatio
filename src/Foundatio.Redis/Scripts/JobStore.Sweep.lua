-- KEYS: expiring, index, succeeded index, failed index, cancelled index, then queued, running, name index and the
--       succeeded, failed and cancelled name indexes for each name, then each job
-- ARGV: name count, the names, then id and name ordinal (0 when the job was not found) for each job
-- Removes each job whose retention has run out, with its index entries, and drops the expiring entries of jobs that
-- are gone or no longer expire. Skips a job whose name is not the one given (it changed since the caller read it).
-- Returns how many jobs were removed.
local nameCount = tonumber(ARGV[1])
local jobKeys = 5 + 6 * nameCount
local finished = { [SUCCEEDED] = 0, [FAILED] = 1, [CANCELLED] = 2 }
local removed, candidate = 0, 0
for i = 2 + nameCount, #ARGV, 2 do
    candidate = candidate + 1
    local id, n = ARGV[i], tonumber(ARGV[i + 1])
    local key = KEYS[jobKeys + candidate]
    local job = redis.call('HMGET', key, 'name', 'status', 'index', 'expires')
    if not job[1] then
        redis.call('ZREM', KEYS[1], id)
    elseif n > 0 and job[1] == ARGV[1 + n] then
        local offset = finished[job[2]]
        if offset and isExpired(job[4]) then
            local nameKeys = 5 + 6 * (n - 1)
            redis.call('ZREM', KEYS[2], job[3])
            redis.call('ZREM', KEYS[3 + offset], job[3])
            redis.call('ZREM', KEYS[nameKeys + 3], job[3])
            redis.call('ZREM', KEYS[nameKeys + 4 + offset], job[3])
            redis.call('ZREM', KEYS[nameKeys + 1], id)
            redis.call('ZREM', KEYS[nameKeys + 2], id)
            redis.call('ZREM', KEYS[1], id)
            redis.call('DEL', key)
            removed = removed + 1
        elseif not isExpired(job[4]) then
            local score = redis.call('ZSCORE', KEYS[1], id)
            if score and tonumber(score) <= now then
                redis.call('ZREM', KEYS[1], id)
            end
        end
    end
end
return removed

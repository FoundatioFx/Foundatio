-- KEYS: queued, running, concurrency keys (the name's unfinished jobs' concurrency keys, by id), holders and waiting
--       for each name
-- ARGV: limit, then the most candidates to take of each name
-- Returns whether the queued jobs were only partly scanned, then the jobs that have been due longest (queued and
-- available, or running with an expired lease) as id, name ordinal, '1' when queued, up to limit of them. A due queued
-- job whose concurrency key another job holds moves to the waiting set, as a claim would, so the candidates are jobs a
-- claim can take now and a claim of 8 isn't spent parking jobs; of several due jobs with one free key, only the one due
-- longest is a candidate, so a key's jobs are claimed in due order.
local NAME_KEYS = 5
local PAGE = 32
local MAX_SCANNED = 1000
local limit = tonumber(ARGV[1])
local candidates = {}
local partial = 0
for n = 1, #KEYS / NAME_KEYS do
    local base = NAME_KEYS * (n - 1)
    local queued, running, concurrencyKeys, holders, waiting = KEYS[base + 1], KEYS[base + 2], KEYS[base + 3], KEYS[base + 4], KEYS[base + 5]
    local max = tonumber(ARGV[1 + n])
    local due = redis.call('ZRANGEBYSCORE', running, '-inf', now, 'WITHSCORES', 'LIMIT', 0, max)
    for i = 1, #due, 2 do
        candidates[#candidates + 1] = { due[i], tonumber(due[i + 1]), n, '0' }
    end

    local taken, offset, scanned, seen = 0, 0, 0, {}
    while taken < max do
        if scanned >= MAX_SCANNED then
            partial = 1
            break
        end
        due = redis.call('ZRANGEBYSCORE', queued, '-inf', now, 'WITHSCORES', 'LIMIT', offset, PAGE)
        if #due == 0 then
            break
        end
        for i = 1, #due, 2 do
            scanned = scanned + 1
            local id, available = due[i], due[i + 1]
            local concurrencyKey = redis.call('HGET', concurrencyKeys, id)
            local holder = concurrencyKey and redis.call('HGET', holders, concurrencyKey)
            if holder and holder ~= id then
                redis.call('ZREM', queued, id)
                redis.call('ZADD', waiting, 0, waitingMember(concurrencyKey, available, id))
            else
                offset = offset + 1
                if not (concurrencyKey and seen[concurrencyKey]) then
                    if concurrencyKey then
                        seen[concurrencyKey] = true
                    end
                    candidates[#candidates + 1] = { id, tonumber(available), n, '1' }
                    taken = taken + 1
                    if taken >= max then
                        break
                    end
                end
            end
        end
    end
end

table.sort(candidates, function(a, b) return a[2] < b[2] end)
local result = { partial }
for i = 1, math.min(limit, #candidates) do
    result[#result + 1] = candidates[i][1]
    result[#result + 1] = candidates[i][3]
    result[#result + 1] = candidates[i][4]
end
return result

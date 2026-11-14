-- KEYS: queued and running for each name, in pairs
-- ARGV: limit
-- Returns the jobs that have been due longest (queued and available, or running with an expired lease) as
-- id, name ordinal, '1' when queued, up to limit of them. Changes nothing.
local limit = tonumber(ARGV[1])
local candidates = {}
for n = 1, #KEYS / 2 do
    for set = 0, 1 do
        local due = redis.call('ZRANGEBYSCORE', KEYS[2 * n - 1 + set], '-inf', now, 'WITHSCORES', 'LIMIT', 0, limit)
        for i = 1, #due, 2 do
            candidates[#candidates + 1] = { due[i], tonumber(due[i + 1]), n, set == 0 and '1' or '0' }
        end
    end
end

table.sort(candidates, function(a, b) return a[2] < b[2] end)
local result = {}
for i = 1, math.min(limit, #candidates) do
    result[#result + 1] = candidates[i][1]
    result[#result + 1] = candidates[i][3]
    result[#result + 1] = candidates[i][4]
end
return result

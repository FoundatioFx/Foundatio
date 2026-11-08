-- KEYS: index
-- ARGV: prefix, continuation member (empty for the first page), limit
local limit = tonumber(ARGV[3])
removeExpired(100)

local start = ARGV[2] == '' and '+' or '(' .. ARGV[2]
local members = redis.call('ZREVRANGEBYLEX', KEYS[1], start, '-', 'LIMIT', 0, limit + 1)
local jobs = {}
for i = 1, math.min(limit, #members) do
    local id = string.sub(members[i], string.find(members[i], '|', 1, true) + 1)
    local key = jobKey(id)
    if not isExpired(redis.call('HGET', key, 'expires')) then
        local job = redis.call('HGETALL', key)
        if #job > 0 then
            jobs[#jobs + 1] = job
        end
    end
end

return { jobs, #members > limit and members[limit] or '' }

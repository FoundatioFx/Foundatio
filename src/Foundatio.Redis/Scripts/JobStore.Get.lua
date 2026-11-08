-- KEYS: job
-- ARGV: prefix
local job = redis.call('HGETALL', KEYS[1])
if #job == 0 or isExpired(redis.call('HGET', KEYS[1], 'expires')) then
    return false
end
return job

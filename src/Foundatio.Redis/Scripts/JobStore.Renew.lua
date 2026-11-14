-- KEYS: job, running
-- ARGV: id, claim token, lease us, name
local job = redis.call('HMGET', KEYS[1], 'status', 'token', 'name', 'cancel')
if job[1] ~= RUNNING or job[2] ~= ARGV[2] or job[3] ~= ARGV[4] then
    return 0
end

local leaseExpires = now + tonumber(ARGV[3])
redis.call('HSET', KEYS[1], 'lease', str(leaseExpires), 'updated', str(now))
redis.call('ZADD', KEYS[2], leaseExpires, ARGV[1])
return job[4] == '1' and 2 or 1

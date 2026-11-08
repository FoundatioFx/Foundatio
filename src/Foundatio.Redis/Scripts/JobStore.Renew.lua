-- KEYS: job
-- ARGV: prefix, id, claim token, lease us
local job = redis.call('HMGET', KEYS[1], 'status', 'token', 'name', 'cancel')
if job[1] ~= RUNNING or job[2] ~= ARGV[3] then
    return 0
end

local leaseExpires = now + tonumber(ARGV[4])
redis.call('HSET', KEYS[1], 'lease', str(leaseExpires), 'updated', str(now))
redis.call('ZADD', runningKey(job[3]), leaseExpires, ARGV[2])
return job[4] == '1' and 2 or 1

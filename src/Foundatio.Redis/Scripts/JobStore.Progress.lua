-- KEYS: job
-- ARGV: prefix, claim token, has percent, percent, has message, message
local job = redis.call('HMGET', KEYS[1], 'status', 'token')
if job[1] ~= RUNNING or job[2] ~= ARGV[2] then
    return 0
end

redis.call('HSET', KEYS[1], 'updated', str(now))
if ARGV[3] == '1' then
    redis.call('HSET', KEYS[1], 'progress', ARGV[4])
end
if ARGV[5] == '1' then
    redis.call('HSET', KEYS[1], 'message', ARGV[6])
end
return 1

-- KEYS: job
-- ARGV: claim token, has percent, percent, has message, message
local job = redis.call('HMGET', KEYS[1], 'status', 'token')
if job[1] ~= RUNNING or job[2] ~= ARGV[1] then
    return 0
end

redis.call('HSET', KEYS[1], 'updated', str(now))
if ARGV[2] == '1' then
    redis.call('HSET', KEYS[1], 'progress', ARGV[3])
end
if ARGV[4] == '1' then
    redis.call('HSET', KEYS[1], 'message', ARGV[5])
end
return 1

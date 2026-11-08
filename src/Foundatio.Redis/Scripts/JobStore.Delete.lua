-- KEYS: job
-- ARGV: prefix, id
if redis.call('EXISTS', KEYS[1]) == 0 then
    return 0
end

local expired = isExpired(redis.call('HGET', KEYS[1], 'expires'))
removeJob(ARGV[2])
return expired and 0 or 1

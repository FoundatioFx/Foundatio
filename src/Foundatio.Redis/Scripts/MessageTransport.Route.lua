-- KEYS: subscriptions, instances, then one deduplication key per message that has one
-- ARGV: deduplication window ms
local subscriptions, instances = KEYS[1], KEYS[2]
local now = nowMs()

local stale = redis.call('ZRANGEBYSCORE', instances, '-inf', now)
if #stale > 0 then
    redis.call('ZREMRANGEBYSCORE', instances, '-inf', now)
end

local accepted = {}
for i = 3, #KEYS do
    accepted[#accepted + 1] = redis.call('SET', KEYS[i], '1', 'NX', 'PX', tonumber(ARGV[1])) and 1 or 0
end

return { redis.call('SMEMBERS', subscriptions), redis.call('ZRANGE', instances, 0, -1), stale, accepted }

-- KEYS: expiring
-- ARGV: limit
-- Returns the ids of up to limit jobs whose retention has run out. Changes nothing.
return redis.call('ZRANGEBYSCORE', KEYS[1], '-inf', now, 'LIMIT', 0, tonumber(ARGV[1]))

-- KEYS: instances
-- ARGV: instance name, timeout ms
-- Returns 1 when the instance was missing, because it is new or because it was removed as stale, and 0 otherwise
return redis.call('ZADD', KEYS[1], nowMs() + tonumber(ARGV[2]), ARGV[1])

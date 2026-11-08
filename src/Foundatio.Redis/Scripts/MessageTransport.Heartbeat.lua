-- KEYS: instances
-- ARGV: instance name, timeout ms
redis.call('ZADD', KEYS[1], nowMs() + tonumber(ARGV[2]), ARGV[1])
return 1

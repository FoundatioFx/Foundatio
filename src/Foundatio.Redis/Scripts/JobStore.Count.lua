-- KEYS: index
-- ARGV: prefix
removeExpired(1000)
return redis.call('ZCARD', KEYS[1])

-- Fallback for pre-8.4 Redis servers and other forks (e.g. Valkey), where native
-- DELEX @key IFEQ @expected (SE.Redis 2.10.1+ ValueCondition) is unavailable. See RedisCapabilities.SupportsCompareAndDelete.
if redis.call('get', @key) == @expected then
  return redis.call('del', @key)
else
  return 0
end
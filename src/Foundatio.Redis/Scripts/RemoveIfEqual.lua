-- NOTE: When Redis 8.4+ becomes the minimum supported version, this script can be replaced
-- with native CAS/CAD: DEL @key IFEQ @expected (SE.Redis 2.10.1+ supports this via ValueCondition).
if redis.call('get', @key) == @expected then
  return redis.call('del', @key)
else
  return 0
end
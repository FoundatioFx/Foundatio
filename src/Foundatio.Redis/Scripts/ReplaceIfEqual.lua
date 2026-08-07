-- Fallback for pre-8.4 Redis and pre-8.1 Valkey (SET ... IFEQ), and other forks, where native compare-and-swap
-- via ValueCondition is unavailable. See RedisCapabilities.SupportsCompareAndSwap.
-- Only replaces when the current value matches @expected; an absent key does not match (mirrors ICacheClient's
-- documented contract and InMemoryCacheClient, which also returns false rather than creating the key).
local currentVal = redis.call('get', @key)
if (currentVal == @expected) then
  if (@expires ~= nil and @expires ~= '') then
    return redis.call('set', @key, @value, 'PX', @expires) and 1 or 0
  else
    -- No expiration specified - plain SET removes any existing TTL
    return redis.call('set', @key, @value) and 1 or 0
  end
else
  return -1
end

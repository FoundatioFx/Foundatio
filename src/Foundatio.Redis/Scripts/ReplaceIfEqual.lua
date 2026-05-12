-- NOTE: When Redis 8.4+ becomes the minimum supported version, this script can be replaced
-- with native CAS/CAD: SET @key @value IFEQ @expected [PX @expires] (SE.Redis 2.10.1+ supports this via ValueCondition).
local currentVal = redis.call('get', @key)
if (currentVal == false or currentVal == @expected) then
  if (@expires ~= nil and @expires ~= '') then
    return redis.call('set', @key, @value, 'PX', @expires) and 1 or 0
  else
    -- No expiration specified - plain SET removes any existing TTL
    return redis.call('set', @key, @value) and 1 or 0
  end
else
  return -1
end

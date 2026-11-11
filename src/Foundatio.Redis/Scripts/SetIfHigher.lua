local c = tonumber(redis.call('get', @key))
local v = tonumber(@value)
if c then
  if v > c then
    if (@expires ~= nil and @expires ~= '') then
      redis.call('set', @key, @value, 'PX', math.ceil(@expires))
    else
      -- No expiration specified - plain SET removes any existing TTL
      redis.call('set', @key, @value)
    end
    return tostring(v - c)
  else
    return '0'
  end
else
  -- Key doesn't exist - create with SET
  if (@expires ~= nil and @expires ~= '') then
    redis.call('set', @key, @value, 'PX', math.ceil(@expires))
  else
    -- New key, no expiration (plain SET removes any TTL)
    redis.call('set', @key, @value)
  end
  return tostring(@value)
end

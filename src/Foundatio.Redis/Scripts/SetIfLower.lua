local c = tonumber(redis.call('get', @key))
if c then
  if tonumber(@value) < c then
    redis.call('set', @key, @value)
    if (@expires ~= nil and @expires ~= '') then
      redis.call('expire', @key, math.ceil(@expires))
    end
    return c - tonumber(@value)
  else
    return 0
  end
else
  redis.call('set', @key, @value)
  if (@expires ~= nil and @expires ~= '') then
    redis.call('expire', @key, math.ceil(@expires))
  end
  return tonumber(@value)
end
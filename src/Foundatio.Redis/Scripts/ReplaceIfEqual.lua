local currentVal = redis.call('get', @key)
if (currentVal == false or currentVal == @expected) then
  if (@expires ~= nil and @expires ~= '') then
    return redis.call('set', @key, @value, 'PX', @expires) and 1 or 0
  else
    return redis.call('set', @key, @value) and 1 or 0
  end
else
  return -1
end
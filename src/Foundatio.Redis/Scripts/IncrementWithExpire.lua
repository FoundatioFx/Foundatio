if math.modf(@value) == 0 then
  local v = redis.call('incrby', @key, @value)
  if (@expires ~= nil and @expires ~= '') then
    redis.call('expire', @key, math.ceil(@expires))
  end
  return tonumber(v)
else
  local v = redis.call('incrbyfloat', @key, @value)
  if (@expires ~= nil and @expires ~= '') then
    redis.call('expire', @key, math.ceil(@expires))
  end
  return tonumber(v)
end
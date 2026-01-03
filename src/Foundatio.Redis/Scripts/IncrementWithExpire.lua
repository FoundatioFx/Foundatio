local integral, fractional = math.modf(@value)
if fractional == 0 then
  local v = redis.call('incrby', @key, @value)
  redis.call('pexpire', @key, math.ceil(@expires))
  return v
else
  local v = redis.call('incrbyfloat', @key, @value)
  redis.call('pexpire', @key, math.ceil(@expires))
  return v
end

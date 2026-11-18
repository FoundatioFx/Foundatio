local integral, fractional = math.modf(@value)
local v
if fractional == 0 then
  v = redis.call('incrby', @key, @value)
else
  v = redis.call('incrbyfloat', @key, @value)
end

if (@expires ~= nil and @expires ~= '') then
  redis.call('pexpire', @key, math.ceil(@expires))
else
  redis.call('persist', @key)
end

return v

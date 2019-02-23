local remove = redis.call('keys', @keys)
if unpack(remove) ~= nil then
  return redis.call('del', unpack(remove))
else
  return 0
end
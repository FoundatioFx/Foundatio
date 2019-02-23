if redis.call('get', @key) == @expected then
  return redis.call('del', @key)
else
  return 0
end
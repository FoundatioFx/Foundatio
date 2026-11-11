-- KEYS: every key of the source
return redis.call('UNLINK', unpack(KEYS))

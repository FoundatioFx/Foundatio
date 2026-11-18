-- Get expiration times for multiple keys using Redis PTTL command.
-- KEYS: All keys to check expiration for
-- Returns: Array of TTL values in milliseconds (in same order as KEYS)
--
-- PTTL return values (per Redis documentation https://redis.io/docs/latest/commands/pttl/):
--   -2 = Key does not exist
--   -1 = Key exists but has no associated expiration
--   Positive integer = Remaining TTL in milliseconds

local result = {}

for i = 1, #KEYS do
    local ttl = redis.call('pttl', KEYS[i])
    table.insert(result, ttl)
end

return result

-- Get expiration times for multiple keys
-- KEYS: All keys to check expiration for
-- Returns: Array of TTL values in milliseconds (in same order as KEYS)
--          Returns -2 for non-existent keys
--          Returns -1 for keys without expiration
--          Returns positive number for keys with expiration

local result = {}

for i = 1, #KEYS do
    local ttl = redis.call('pttl', KEYS[i])
    table.insert(result, ttl)
end

return result

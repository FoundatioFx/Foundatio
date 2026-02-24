-- Set expiration times for multiple keys using Redis PEXPIRE and PERSIST commands.
-- KEYS: All keys to set expiration for
-- ARGV: TTL values in milliseconds corresponding to each key in KEYS
--       Negative = Remove expiration (persist key indefinitely)
--       0 = Delete key immediately (PEXPIRE 0 causes Redis to delete the key)
--       Positive integer = Set expiration to this many milliseconds
--
-- Uses PEXPIRE for setting TTL (https://redis.io/docs/latest/commands/pexpire/)
-- Uses PERSIST for removing TTL (https://redis.io/docs/latest/commands/persist/)

for i = 1, #KEYS do
    local ttl = tonumber(ARGV[i])

    if ttl == nil or ttl < 0 then
        redis.call('persist', KEYS[i])
    else
        redis.call('pexpire', KEYS[i], math.ceil(ttl))
    end
end

return true

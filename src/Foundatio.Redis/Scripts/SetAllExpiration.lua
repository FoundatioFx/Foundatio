-- Set expiration times for multiple keys
-- KEYS: All keys to set expiration for
-- ARGV: TTL values in milliseconds corresponding to each key in KEYS
--       Use -1 or 0 to remove expiration (persist)
--       Positive values set the expiration

for i = 1, #KEYS do
    local ttl = tonumber(ARGV[i])

    if ttl == nil or ttl <= 0 then
        redis.call('persist', KEYS[i])
    else
        redis.call('pexpire', KEYS[i], math.ceil(ttl))
    end
end

return true

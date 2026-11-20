-- KEYS: stream, leases, retries, delayed, held, dead-letter stream
-- ARGV: consumer group, dead letters to keep (0 = all), dead-letter retention ms (0 = forever)
-- Returns the queued, delayed, in-flight and dead-lettered counts. Creates no key, so a missing source counts as empty.
local stream, leases, retries, delayed, held, deadLetters = KEYS[1], KEYS[2], KEYS[3], KEYS[4], KEYS[5], KEYS[6]
local now = nowMs()

-- The entries the group has not read yet: its lag where Redis reports one, else the entries that are not pending,
-- since a settled entry is acknowledged and deleted at once. Redis 6.2 has no lag, and 7+ cannot tell it after some
-- deletes, such as completing the last entry read while an earlier one is still pending.
local length = redis.call('XLEN', stream)
local unread = 0
if length > 0 then
    unread = length
    local groups = redis.pcall('XINFO', 'GROUPS', stream)
    if not isError(groups) then
        for _, group in ipairs(groups) do
            local info = {}
            for i = 1, #group, 2 do
                info[group[i]] = group[i + 1]
            end
            if info['name'] == ARGV[1] then
                if info['lag'] then
                    unread = tonumber(info['lag'])
                else
                    unread = math.max(0, length - tonumber(info['pending']))
                end
            end
        end
    end
end

-- A lease that lapsed, or an abandoned message that is due, waits for the next receive; a retry's lease is its delay.
local lapsed = redis.call('ZCOUNT', leases, '-inf', now)
local leased = redis.call('ZCOUNT', leases, '(' .. now, '+inf')
local retrying = redis.call('ZCOUNT', retries, '(' .. now, '+inf')
local due = redis.call('ZCOUNT', delayed, '-inf', now)
local waiting = redis.call('ZCOUNT', delayed, '(' .. now, '+inf')
trimDeadLetters(deadLetters, tonumber(ARGV[2]), tonumber(ARGV[3]))

return {
    unread + lapsed + due + redis.call('HLEN', held),
    waiting + retrying,
    math.max(0, leased - retrying),
    redis.call('XLEN', deadLetters)
}

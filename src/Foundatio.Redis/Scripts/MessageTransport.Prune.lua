-- KEYS: stream, idle
-- ARGV: group, released consumer, minimum idle ms
-- Removes consumers with no pending messages that have not read for the minimum idle time: receivers that stopped
-- without releasing themselves. Returns the number removed
local stream, group, released = KEYS[1], ARGV[1], ARGV[2]
local minIdle = tonumber(ARGV[3])
local consumers = redis.pcall('XINFO', 'CONSUMERS', stream, group)
if isError(consumers) then
    return 0
end

local removed = 0
for _, consumer in ipairs(consumers) do
    local info = {}
    for i = 1, #consumer, 2 do
        info[consumer[i]] = consumer[i + 1]
    end

    if info['name'] ~= released and tonumber(info['pending']) == 0 and tonumber(info['idle']) >= minIdle then
        redis.call('XGROUP', 'DELCONSUMER', stream, group, info['name'])
        redis.call('ZREM', KEYS[2], info['name'])
        removed = removed + 1
    end
end

return removed

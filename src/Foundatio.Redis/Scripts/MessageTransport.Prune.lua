-- KEYS: stream
-- ARGV: group, released consumer, minimum idle ms
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
        removed = removed + 1
    end
end

return removed
